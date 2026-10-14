using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhotoFinder.Services;

/// <summary>What to change in a photo's metadata. Null leaves a field as it is; empty clears it (a present, empty value).</summary>
public sealed record MetadataChanges
{
    /// <summary>dc:description, the caption.</summary>
    public string? Caption { get; init; }
    /// <summary>dc:title, Explorer's Title when the file's own title is empty.</summary>
    public string? Title { get; init; }
    public string? AltText { get; init; }
    public string? ExtendedDescription { get; init; }
    /// <summary>The rating in stars, 0 to 5; 0 removes it.</summary>
    public int? Stars { get; init; }
    /// <summary>The file's own title fields (EXIF ImageDescription and XPTitle, a PNG's Title text) holding exactly this
    /// are emptied, so Explorer's Title follows the XMP instead of an old caption.</summary>
    public string? ClearOwnTitle { get; init; }

    public bool IsEmpty => Caption is null && Title is null && AltText is null && ExtendedDescription is null && Stars is null;
}

/// <summary>
/// An XMP packet, read, changed and written back, for <see cref="JpegMetadata"/> and <see cref="PngMetadata"/>: only the
/// fields asked for change, and everything else in it (other apps' properties, other languages) is kept. Bytes that
/// aren't UTF-8, or text that isn't XML, are refused rather than rewritten.
/// </summary>
public static class XmpPacket
{
    internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Meta = "adobe:ns:meta/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Iptc = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace MicrosoftPhoto = "http://ns.microsoft.com/photo/1.0/";

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Room left in a grown packet for later changes, as XMP's guidance suggests.</summary>
    private const int Room = 2048;

    public static XDocument Parse(ReadOnlySpan<byte> packet)
    {
        try
        {
            // Strictly: bytes that aren't UTF-8 would otherwise come back as replacement characters, changing another
            // app's data.
            var text = StrictUtf8.GetString(packet).TrimEnd('\0', ' ', '\r', '\n', '\t');
            var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            if (document.Descendants(Rdf + "RDF").FirstOrDefault() is null) throw new XmlException("No rdf:RDF.");
            return document;
        }
        catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
        {
            throw new MetadataRefusedException("Its existing metadata (XMP) can't be read.");
        }
    }

    public static XDocument New() =>
        new(new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));

    /// <summary>The caption, alt text and extended description: a lang-alt's default language (or its only one), or a
    /// plain value an older tool wrote. Null for each that isn't there.</summary>
    public static (string? Caption, string? AltText, string? ExtendedDescription) Fields(XDocument document)
    {
        var descriptions = document.Descendants(Rdf + "Description").ToList();
        string? Get(XName name)
        {
            foreach (var d in descriptions)
            {
                if (d.Attribute(name) is { } attribute) return attribute.Value.Trim();
                if (d.Element(name) is not { } element) continue;
                if (element.Element(Rdf + "Alt") is not { } alt) return element.Value.Trim();
                var items = alt.Elements(Rdf + "li").ToList();
                var item = items.FirstOrDefault(li => string.Equals((string?)li.Attribute(XNamespace.Xml + "lang"), "x-default", StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault();
                return item?.Value.Trim() ?? "";
            }
            return null;
        }
        return (Get(Dc + "description"), Get(Iptc + "AltTextAccessibility"), Get(Iptc + "ExtDescrAccessibility"));
    }

    /// <summary>The properties a change writes: the caption, Title, accessibility fields, and both ratings.</summary>
    internal static IEnumerable<XName> Written(MetadataChanges changes)
    {
        if (changes.Caption is not null) yield return Dc + "description";
        if (changes.Title is not null) yield return Dc + "title";
        if (changes.AltText is not null) yield return Iptc + "AltTextAccessibility";
        if (changes.ExtendedDescription is not null) yield return Iptc + "ExtDescrAccessibility";
        if (changes.Stars is not null)
        {
            yield return Xmp + "Rating";
            yield return MicrosoftPhoto + "Rating";
        }
    }

    /// <summary>Whether the packet holds <paramref name="name"/>, as an attribute or an element of any rdf:Description.</summary>
    internal static bool Holds(XDocument document, XName name) =>
        document.Descendants(Rdf + "Description").Any(d => d.Attribute(name) is not null || d.Element(name) is not null);

    /// <summary>xmpNote:HasExtendedXMP: the GUID naming an Extended XMP packet, or null.</summary>
    internal static string? ExtendedGuid(XDocument document)
    {
        XName name = XNamespace.Get("http://ns.adobe.com/xmp/note/") + "HasExtendedXMP";
        foreach (var d in document.Descendants(Rdf + "Description"))
        {
            // As written: the parts must name exactly the same GUID.
            if (d.Attribute(name) is { } a) return a.Value;
            if (d.Element(name) is { } e) return e.Value;
        }
        return null;
    }

    public static void Edit(XDocument document, MetadataChanges changes)
    {
        var rdf = document.Descendants(Rdf + "RDF").First();
        var descriptions = rdf.Elements(Rdf + "Description").ToList();
        if (descriptions.Count == 0)
        {
            var added = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(added);
            descriptions.Add(added);
        }
        var target = descriptions[0];

        void Declare(XNamespace ns, string prefix)
        {
            if (target.GetPrefixOfNamespace(ns) is null) target.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        }

        void Remove(XName name)
        {
            foreach (var d in descriptions)
            {
                d.Attribute(name)?.Remove();
                d.Elements(name).Remove();
            }
        }

        void LangAlt(XName name, string prefix, string? value)
        {
            if (value is null) return;
            value = value.Trim();
            // An existing language alternative keeps its other languages; only the default one changes.
            var existing = descriptions.SelectMany(d => d.Elements(name)).FirstOrDefault(e => e.Element(Rdf + "Alt") is not null);
            if (existing is not null)
            {
                foreach (var d in descriptions) d.Attribute(name)?.Remove();
                var alt = existing.Element(Rdf + "Alt")!;
                var items = alt.Elements(Rdf + "li").ToList();
                var item = items.FirstOrDefault(li => string.Equals((string?)li.Attribute(XNamespace.Xml + "lang"), "x-default", StringComparison.OrdinalIgnoreCase));
                // The text being replaced: the default's, or a single language's when there's no default. Items holding
                // it are the same caption, so they change too; translations that differ are kept.
                var old = item?.Value ?? (items.Count == 1 ? items[0].Value : null);
                if (old is not null)
                    foreach (var li in items.Where(li => li != item && li.Value == old)) li.Value = value;
                if (item is null) alt.AddFirst(item = new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default")));
                item.Value = value;
                return;
            }
            Remove(name);
            Declare(name.Namespace, prefix);
            target.Add(new XElement(name, new XElement(Rdf + "Alt",
                new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value))));
        }

        void Simple(XName name, string prefix, string? value)
        {
            Remove(name);
            if (value is null) return;
            Declare(name.Namespace, prefix);
            target.Add(new XElement(name, value));
        }

        LangAlt(Dc + "description", "dc", changes.Caption);
        LangAlt(Dc + "title", "dc", changes.Title);
        LangAlt(Iptc + "AltTextAccessibility", "Iptc4xmpCore", changes.AltText);
        LangAlt(Iptc + "ExtDescrAccessibility", "Iptc4xmpCore", changes.ExtendedDescription);
        if (changes.Stars is { } stars)
        {
            // Both of the ratings Windows reads; none for no rating.
            Simple(Xmp + "Rating", "xmp", stars > 0 ? stars.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
            Simple(MicrosoftPhoto + "Rating", "MicrosoftPhoto",
                stars > 0 ? Models.MediaTypes.RatingFromStars(stars).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
    }

    /// <summary>The packet, in XMP's wrapper. With <paramref name="oldLength"/> it's padded to that size when it fits,
    /// otherwise given room to grow, never past <paramref name="maxLength"/>; without, it has no padding.</summary>
    public static byte[] Serialize(XDocument document, int oldLength = 0, int maxLength = int.MaxValue, bool padded = true)
    {
        var root = document.Root!;
        // Carriage returns in values stay escaped (&#xD;), or a reader would normalise them away.
        var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Entitize,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
            root.WriteTo(writer);
        var body = buffer.ToArray();
        var head = Encoding.UTF8.GetBytes("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        var tail = Encoding.UTF8.GetBytes("<?xpacket end=\"w\"?>");
        var bare = head.Length + body.Length + tail.Length;
        var size = !padded ? bare : bare <= oldLength ? oldLength : Math.Min(bare + Room, maxLength);
        var padding = Math.Max(0, size - bare);
        var packet = new byte[bare + padding];
        head.CopyTo(packet, 0);
        body.CopyTo(packet, head.Length);
        // Spaces, with a line break every hundred, as XMP's padding is written.
        for (var i = 0; i < padding; i++) packet[head.Length + body.Length + i] = (byte)(i % 100 == 99 ? '\n' : ' ');
        tail.CopyTo(packet, bare + padding - tail.Length);
        return packet;
    }

    // MARK: EXIF, in place (JPEG's APP1 and PNG's eXIf hold the same TIFF data)

    /// <summary>An EXIF text value, read as UTF-8 when it is that, and as Latin-1.</summary>
    internal static IEnumerable<string> TextOf(byte[] b, int at, int n)
    {
        string? utf8 = null;
        try
        {
            utf8 = StrictUtf8.GetString(b, at, n);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8.
        }
        if (utf8 is not null) yield return utf8.TrimEnd('\0').Trim();
        yield return Encoding.Latin1.GetString(b, at, n).TrimEnd('\0').Trim();
    }

    /// <summary>In the TIFF data at <paramref name="tiff"/> up to <paramref name="end"/>: the rating tags Windows writes
    /// (Rating, RatingPercent) set where they exist, and title fields holding <see cref="MetadataChanges.ClearOwnTitle"/>
    /// emptied. Only values the same size; nothing is added or moved. True when anything changed.</summary>
    /// <param name="clearDescription">ImageDescription is emptied whatever it holds (a screenshot's "Screenshot").</param>
    internal static bool PatchExif(byte[] b, int tiff, int end, MetadataChanges changes, bool clearDescription = false)
    {
        if (tiff + 8 > end) return false;
        bool little;
        if (b[tiff] == 'I' && b[tiff + 1] == 'I') little = true;
        else if (b[tiff] == 'M' && b[tiff + 1] == 'M') little = false;
        else return false;
        int U16(int at) => little ? b[at] | (b[at + 1] << 8) : (b[at] << 8) | b[at + 1];
        int U32(int at) => little ? b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)
            : (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        var changed = false;
        void Put16(int at, int v)
        {
            var (hi, lo) = ((byte)(v >> 8), (byte)v);
            var (first, second) = little ? (lo, hi) : (hi, lo);
            changed |= b[at] != first || b[at + 1] != second;
            b[at] = first;
            b[at + 1] = second;
        }

        var ifd0 = tiff + U32(tiff + 4);
        if (ifd0 < tiff || ifd0 + 2 > end) return false;
        var count = U16(ifd0);
        for (var k = 0; k < count; k++)
        {
            var entry = ifd0 + 2 + 12 * k;
            if (entry + 12 > end) break;
            var tag = U16(entry);
            var type = U16(entry + 2);
            var n = U32(entry + 4);
            if (changes.Stars is { } stars && type == 3 && n == 1)
            {
                if (tag == 0x4746) Put16(entry + 8, Math.Clamp(stars, 0, 5));
                else if (tag == 0x4749) Put16(entry + 8, (int)Models.MediaTypes.RatingFromStars(stars));
            }
            // ImageDescription (ASCII) and XPTitle (UTF-16 bytes): emptied when they hold the old title, and ImageDescription
            // whatever it holds with clearDescription.
            var old = changes.ClearOwnTitle ?? "";
            if ((old.Length > 0 || (clearDescription && tag == 0x010E)) && ((tag == 0x010E && type == 2) || (tag == 0x9C9B && type == 1)) && n > 0)
            {
                var at = n <= 4 ? entry + 8 : tiff + U32(entry + 8);
                if (n < 0 || at < tiff || n > end - at) continue;
                // Windows writes ImageDescription as UTF-8; older cameras and tools as Latin-1.
                var matches = tag == 0x010E
                    ? clearDescription || TextOf(b, at, n).Any(t => t == old.Trim())
                    : Encoding.Unicode.GetString(b, at, n - n % 2).TrimEnd('\0').Trim() == old.Trim();
                if (matches)
                {
                    Array.Clear(b, at, n);
                    changed = true;
                }
            }
        }
        return changed;
    }
}

/// <summary>A photo whose metadata can't be changed safely, with why (Extended XMP, XMP that can't be read, too much to
/// fit, a file that can't be followed). The photo is left as it is.</summary>
public sealed class MetadataRefusedException(string message) : Exception(message);
