using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhotoFinder.Services;

/// <summary>
/// Changes a JPEG's metadata by editing its segments (#86), as the Mac does since #81: only the XMP segment is replaced
/// (or added), and a few EXIF values are patched in place without changing their size. Every other byte is copied as it
/// was: the EXIF block with its thumbnail and MakerNote, comments, ICC profiles, the compressed picture, and whatever
/// follows it (a motion photo's video, an Ultra HDR gain map). WIC and Windows' property handler rebuild EXIF instead,
/// which re-encodes the thumbnail, drops tags and moves MakerNotes whose offsets then point at the wrong bytes.
///
/// - The XMP keeps its size when the new text fits in its padding, and otherwise grows with room left for next time.
/// - A file with a Multi-Picture index (Ultra HDR, MPO) must keep its size: the index records the main picture's
///   length. When the change doesn't fit, <see cref="SecondPictureException"/>.
/// - Extended XMP (spread over several segments) isn't edited.
/// </summary>
public static class JpegMetadata
{
    /// <summary>What to change. Null leaves a field as it is; empty clears it (a present, empty value).</summary>
    public sealed record Changes
    {
        /// <summary>dc:description, the caption.</summary>
        public string? Caption { get; init; }
        /// <summary>dc:title, Explorer's Title when the EXIF title is empty.</summary>
        public string? Title { get; init; }
        public string? AltText { get; init; }
        public string? ExtendedDescription { get; init; }
        /// <summary>The rating in stars, 0 to 5; 0 removes it.</summary>
        public int? Stars { get; init; }
        /// <summary>EXIF's own title fields (ImageDescription, XPTitle) holding exactly this are emptied in place, so
        /// Explorer's Title follows the XMP instead of an old caption.</summary>
        public string? ClearExifTitle { get; init; }

        public bool IsEmpty => Caption is null && Title is null && AltText is null && ExtendedDescription is null && Stars is null;
    }

    private static readonly byte[] XmpSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] ExtendedSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    private static readonly byte[] ExifSignature = Encoding.ASCII.GetBytes("Exif\0\0");
    private static readonly byte[] MpfSignature = Encoding.ASCII.GetBytes("MPF\0");

    /// <summary>A segment's largest length field: the length counts itself, not the marker.</summary>
    private const int MaxSegmentLength = 65535;
    /// <summary>Room left in a grown XMP packet for later changes, as XMP's guidance suggests.</summary>
    private const int Room = 2048;

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Meta = "adobe:ns:meta/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Iptc = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace MicrosoftPhoto = "http://ns.microsoft.com/photo/1.0/";

    private readonly record struct Segment(byte Marker, int Offset, int Length)
    {
        public int End => Offset + Length;
    }

    /// <summary>The JPEG with the changes made. Throws <see cref="MetadataRefusedException"/> for a file it can't safely
    /// change (not a JPEG it can follow, unreadable XMP, Extended XMP, too much metadata) and
    /// <see cref="SecondPictureException"/> for a multi-picture file the change won't fit in.</summary>
    public static byte[] Apply(byte[] jpeg, Changes changes)
    {
        var segments = Segments(jpeg);
        var xmpAt = segments.FindIndex(s => IsApp1With(jpeg, s, XmpSignature));
        if (segments.Any(s => IsApp1With(jpeg, s, ExtendedSignature)))
            throw new MetadataRefusedException("Its metadata is spread over several parts (Extended XMP), which isn't changed.");
        var mpf = segments.FindIndex(s => s.Marker == 0xE2 && Has(jpeg, s.Offset + 4, MpfSignature));

        XDocument document;
        var oldPacketLength = 0;
        if (xmpAt >= 0)
        {
            var s = segments[xmpAt];
            var start = s.Offset + 4 + XmpSignature.Length;
            oldPacketLength = s.End - start;
            document = Parse(jpeg.AsSpan(start, oldPacketLength));
        }
        else
        {
            document = new XDocument(new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                    new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));
        }
        Edit(document, changes);
        var packet = Packet(document, oldPacketLength);
        var segment = new byte[4 + XmpSignature.Length + packet.Length];
        var length = segment.Length - 2;
        if (length > MaxSegmentLength) throw new MetadataRefusedException("There's too much metadata to fit.");
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)length;
        XmpSignature.CopyTo(segment, 4);
        packet.CopyTo(segment, 4 + XmpSignature.Length);

        // Replaced where it was, or added after the JFIF and EXIF segments at the start, ahead of any Multi-Picture index.
        int cut, keep;
        if (xmpAt >= 0)
        {
            cut = segments[xmpAt].Offset;
            keep = segments[xmpAt].End;
        }
        else
        {
            var after = 0;
            while (after < segments.Count && (segments[after].Marker == 0xE0 || IsApp1With(jpeg, segments[after], ExifSignature))) after++;
            cut = keep = after == 0 ? 2 : segments[after - 1].End;
        }
        var result = new byte[cut + segment.Length + (jpeg.Length - keep)];
        jpeg.AsSpan(0, cut).CopyTo(result);
        segment.CopyTo(result, cut);
        jpeg.AsSpan(keep).CopyTo(result.AsSpan(cut + segment.Length));
        if (mpf >= 0 && result.Length != jpeg.Length) throw new SecondPictureException();

        // EXIF values patched in place, at the same offsets (the EXIF segment comes before the XMP, or moved with it).
        var exif = Segments(result).FirstOrDefault(s => IsApp1With(result, s, ExifSignature));
        if (exif.Length > 0) PatchExif(result, exif, changes);
        return result;
    }

    // MARK: Segments

    /// <summary>The segments before the compressed picture. Throws when the file isn't a JPEG that can be followed.</summary>
    private static List<Segment> Segments(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) throw new MetadataRefusedException("It isn't a JPEG that can be followed.");
        var list = new List<Segment>();
        var i = 2;
        while (true)
        {
            if (i + 3 >= b.Length || b[i] != 0xFF) throw new MetadataRefusedException("It isn't a JPEG that can be followed.");
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xDA or 0xD9) return list;  // the picture starts: nothing after it is touched
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var length = (b[i + 2] << 8) | b[i + 3];
            if (length < 2 || i + 2 + length > b.Length) throw new MetadataRefusedException("It isn't a JPEG that can be followed.");
            list.Add(new Segment(marker, i, 2 + length));
            i += 2 + length;
        }
    }

    private static bool Has(byte[] b, int at, byte[] signature) =>
        at + signature.Length <= b.Length && b.AsSpan(at, signature.Length).SequenceEqual(signature);

    private static bool IsApp1With(byte[] b, Segment s, byte[] signature) =>
        s.Marker == 0xE1 && s.Length >= 4 + signature.Length && Has(b, s.Offset + 4, signature);

    // MARK: XMP

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static XDocument Parse(ReadOnlySpan<byte> packet)
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

    private static void Edit(XDocument document, Changes changes)
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

    /// <summary>The packet, in XMP's wrapper, padded to the old packet's size when it fits, otherwise with room to grow.</summary>
    private static byte[] Packet(XDocument document, int oldLength)
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
        var maxPacket = MaxSegmentLength - 2 - XmpSignature.Length;
        var size = bare <= oldLength ? oldLength : Math.Min(bare + Room, maxPacket);
        var padding = Math.Max(0, size - bare);
        var packet = new byte[bare + padding];
        head.CopyTo(packet, 0);
        body.CopyTo(packet, head.Length);
        // Spaces, with a line break every hundred, as XMP's padding is written.
        for (var i = 0; i < padding; i++) packet[head.Length + body.Length + i] = (byte)(i % 100 == 99 ? '\n' : ' ');
        tail.CopyTo(packet, bare + padding - tail.Length);
        return packet;
    }

    // MARK: EXIF, in place

    /// <summary>An EXIF text value, read as UTF-8 when it is that, and as Latin-1.</summary>
    private static IEnumerable<string> TextOf(byte[] b, int at, int n)
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

    /// <summary>The rating tags Windows writes (Rating, RatingPercent) set where they exist, and EXIF title fields
    /// holding <see cref="Changes.ClearExifTitle"/> emptied. Only values the same size; nothing is added or moved.</summary>
    private static void PatchExif(byte[] b, Segment exif, Changes changes)
    {
        var tiff = exif.Offset + 4 + ExifSignature.Length;
        var end = exif.End;
        if (tiff + 8 > end) return;
        bool little;
        if (b[tiff] == 'I' && b[tiff + 1] == 'I') little = true;
        else if (b[tiff] == 'M' && b[tiff + 1] == 'M') little = false;
        else return;
        int U16(int at) => little ? b[at] | (b[at + 1] << 8) : (b[at] << 8) | b[at + 1];
        int U32(int at) => little ? b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)
            : (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        void Put16(int at, int v)
        {
            if (little) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
            else { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
        }

        var ifd0 = tiff + U32(tiff + 4);
        if (ifd0 < tiff || ifd0 + 2 > end) return;
        var count = U16(ifd0);
        for (var k = 0; k < count; k++)
        {
            var entry = ifd0 + 2 + 12 * k;
            if (entry + 12 > end) return;
            var tag = U16(entry);
            var type = U16(entry + 2);
            var n = U32(entry + 4);
            if (changes.Stars is { } stars && type == 3 && n == 1)
            {
                if (tag == 0x4746) Put16(entry + 8, Math.Clamp(stars, 0, 5));
                else if (tag == 0x4749) Put16(entry + 8, (int)Models.MediaTypes.RatingFromStars(stars));
            }
            // ImageDescription (ASCII) and XPTitle (UTF-16 bytes): emptied when they hold the old title.
            if (changes.ClearExifTitle is { Length: > 0 } old && ((tag == 0x010E && type == 2) || (tag == 0x9C9B && type == 1)) && n > 0)
            {
                var at = n <= 4 ? entry + 8 : tiff + U32(entry + 8);
                if (n < 0 || at < tiff || n > end - at) continue;
                // Windows writes ImageDescription as UTF-8; older cameras and tools as Latin-1.
                var matches = tag == 0x010E
                    ? TextOf(b, at, n).Any(t => t == old.Trim())
                    : Encoding.Unicode.GetString(b, at, n - n % 2).TrimEnd('\0').Trim() == old.Trim();
                if (matches) Array.Clear(b, at, n);
            }
        }
    }
}

/// <summary>A JPEG whose metadata can't be changed safely, with why (Extended XMP, XMP that can't be read, too much to
/// fit). The photo is left as it is.</summary>
public sealed class MetadataRefusedException(string message) : Exception(message);
