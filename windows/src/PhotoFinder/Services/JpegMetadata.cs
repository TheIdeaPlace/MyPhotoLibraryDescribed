using System.Text;
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
/// - A file with a Multi-Picture index (Ultra HDR, MPO, #90) has the index patched: it records the main picture's
///   size, and where the other pictures start, counted from the index itself. When the index can't be followed, or
///   doesn't point at the pictures before and after the change, <see cref="SecondPictureException"/>.
/// - Extended XMP (the rest of a large packet, in segments of its own, as Pixel photos have, #149) is kept byte for byte,
///   as the Mac's #166 does: only the main packet changes, keeping xmpNote:HasExtendedXMP. It's checked first: every part
///   names that GUID, the parts make one packet whose MD5 is the GUID, and none of the fields being written is in it
///   (they'd be read from there instead).
/// </summary>
public static class JpegMetadata
{
    private static readonly byte[] XmpSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] ExtendedSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    private static readonly byte[] ExifSignature = Encoding.ASCII.GetBytes("Exif\0\0");
    private static readonly byte[] MpfSignature = Encoding.ASCII.GetBytes("MPF\0");
    private static readonly byte[] PhotoshopSignature = Encoding.ASCII.GetBytes("Photoshop 3.0\0");
    /// <summary>Image resource signatures: Photoshop's own and the ones other apps use, as ExifTool walks them.</summary>
    private static readonly byte[][] ResourceSignatures = [.. new[] { "8BIM", "PHUT", "DCSR", "AgHg", "MeSa" }.Select(Encoding.ASCII.GetBytes)];

    /// <summary>A segment's largest length field: the length counts itself, not the marker.</summary>
    private const int MaxSegmentLength = 65535;

    private readonly record struct Segment(byte Marker, int Offset, int Length)
    {
        public int End => Offset + Length;
    }

    /// <summary>Whether the bytes start like a JPEG.</summary>
    public static bool LooksLikeJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>The JPEG with the changes made. Throws <see cref="MetadataRefusedException"/> for a file it can't safely
    /// change (not a JPEG it can follow, unreadable XMP, Extended XMP that doesn't add up or holds a field being written,
    /// too much metadata) and
    /// <see cref="SecondPictureException"/> for a multi-picture file whose index can't be kept right.</summary>
    public static byte[] Apply(byte[] jpeg, MetadataChanges changes)
    {
        var segments = Segments(jpeg);
        var xmpAt = segments.FindIndex(s => IsApp1With(jpeg, s, XmpSignature));
        var mpf = segments.FindIndex(s => s.Marker == 0xE2 && Has(jpeg, s.Offset + 4, MpfSignature));
        // Only one index is patched, so a file with two (one would be left wrong) isn't grown.
        var twoIndexes = segments.Count(s => s.Marker == 0xE2 && Has(jpeg, s.Offset + 4, MpfSignature)) > 1;

        XDocument document;
        var oldPacketLength = 0;
        if (xmpAt >= 0)
        {
            var s = segments[xmpAt];
            var start = s.Offset + 4 + XmpSignature.Length;
            oldPacketLength = s.End - start;
            document = XmpPacket.Parse(jpeg.AsSpan(start, oldPacketLength));
        }
        else
        {
            document = XmpPacket.New();
        }
        var extension = segments.Where(s => IsApp1With(jpeg, s, ExtendedSignature)).ToList();
        string? guid = null;
        if (extension.Count > 0)
        {
            // Which main packet names the extension must be clear, as on the Mac.
            if (segments.Count(s => IsApp1With(jpeg, s, XmpSignature)) > 1) throw new MetadataRefusedException(DontAddUp);
            guid = CheckExtension(jpeg, extension, xmpAt >= 0 ? document : null, changes);
        }
        if (changes.Caption is { } caption && StaleIptcCaption(jpeg, segments) is { } old && !old.Any(t => SameText(t, caption)))
            throw new OlderCaptionException();
        XmpPacket.Edit(document, changes);
        // And after the change, the main packet still names it (as the Mac checks), or the extension would be lost.
        if (guid is not null && XmpPacket.ExtendedGuid(document) != guid) throw new MetadataRefusedException(DontAddUp);
        var packet = XmpPacket.Serialize(document, oldPacketLength, MaxSegmentLength - 2 - XmpSignature.Length);
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
        if (mpf >= 0 && result.Length != jpeg.Length)
        {
            if (twoIndexes) throw new SecondPictureException();
            var index = MultiPictureIndex.Read(jpeg, segments[mpf].Offset + 4 + MpfSignature.Length, segments[mpf].End);
            // The XMP comes before the index (as it's placed) or after it: the index moves with the change in the first
            // case, so only the main picture's size changes; in the second the other pictures move away from it too.
            var delta = result.Length - jpeg.Length;
            index.Patch(result, delta, movesWithChange: cut <= segments[mpf].Offset);
        }

        // EXIF values patched in place, at the same offsets (the EXIF segment comes before the XMP, or moved with it).
        var exif = Segments(result).FirstOrDefault(s => IsApp1With(result, s, ExifSignature));
        if (exif.Length > 0) XmpPacket.PatchExif(result, exif.Offset + 4 + ExifSignature.Length, exif.End, changes);
        return result;
    }

    // MARK: Photoshop's IPTC

    /// <summary>The same caption, allowing for the ends trimmed and line breaks written as CR (as Photoshop does).</summary>
    internal static bool SameText(string a, string b) => Lines(a) == Lines(b);

    /// <summary>Line breaks as LF, the ends trimmed.</summary>
    private static string Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>
    /// The caption in Photoshop's IPTC (APP13, image resource 0x0404, dataset 2:120), as UTF-8 and as Latin-1, when readers
    /// would show it ahead of the XMP's: when there's an IPTC digest (resource 0x0425) and it isn't the MD5 of the IPTC
    /// block, an app changed the IPTC after the XMP was written, so ImageIO and others take the IPTC's (#169, as the
    /// Mac's #169, and as the metadata guidelines say). Null when there's no such caption, or the digest matches, or
    /// there's no digest (then the XMP is read). The IPTC itself isn't changed.
    ///
    /// The Photoshop segments are joined first (a long resource list is split to fit, each part repeating the header), and
    /// the first IPTC block and the first digest count, as ExifTool reads them.
    /// </summary>
    private static string[]? StaleIptcCaption(byte[] jpeg, IReadOnlyList<Segment> segments)
    {
        var list = new List<byte>();
        foreach (var s in segments.Where(s => s.Marker == 0xED && Has(jpeg, s.Offset + 4, PhotoshopSignature)))
            list.AddRange(jpeg.AsSpan((s.Offset + 4 + PhotoshopSignature.Length)..s.End));
        return StaleIptcCaption(list.ToArray());
    }

    /// <summary>
    /// The caption other apps read from this JPEG when it's Photoshop's IPTC caption rather than the XMP's (#181): its
    /// digest is stale, as above, and the caption isn't empty (an empty one shows the XMP's, as on the Mac). Line breaks as
    /// LF. Null when the XMP's caption is the one read, or the file can't be followed.
    /// </summary>
    public static string? IptcCaptionReadFirst(Stream file) =>
        IptcCaptionReadings(file)?.Select(Lines).FirstOrDefault(t => t.Length > 0);

    /// <summary>Whether a new caption would be refused (#179): an older IPTC caption, different from it, would still be
    /// read. For saying so before asking anything.</summary>
    public static bool WouldRefuseCaption(Stream file, string caption) =>
        IptcCaptionReadings(file) is { } readings && !readings.Any(t => SameText(t, caption));

    /// <summary>Whether Photoshop's IPTC caption, even an empty one, would still be read instead of any new caption (a
    /// stale digest): then only its own text could be written.</summary>
    public static bool HasOlderCaption(Stream file) => IptcCaptionReadings(file) is not null;

    /// <summary>
    /// The IPTC caption's readings when its digest is stale, reading only the segment headers and Photoshop's segments, not
    /// the picture, so it's cheap for a library scan. Null when there's none, or the file can't be followed. (The same
    /// walk as <see cref="Segments"/>, over a stream.) A read that fails partway (a share that dropped) throws, so the
    /// photo is read again later rather than cached without it.
    /// </summary>
    private static string[]? IptcCaptionReadings(Stream file)
    {
        try
        {
            var photoshop = new List<byte>();
            var header = new byte[4];
            var fileLength = file.Length;
            file.Position = 0;
            if (file.Read(header, 0, 2) != 2 || header[0] != 0xFF || header[1] != 0xD8) return null;
            long at = 2;
            while (at + 4 <= fileLength)
            {
                file.Position = at;
                file.ReadExactly(header);
                if (header[0] != 0xFF) return null;
                var marker = header[1];
                if (marker == 0xFF) { at++; continue; }
                if (marker is 0xDA or 0xD9) break;
                if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { at += 2; continue; }
                var length = (header[2] << 8) | header[3];
                if (length < 2 || at + 2 + length > fileLength) return null;
                if (marker == 0xED && length - 2 > PhotoshopSignature.Length)
                {
                    var body = new byte[length - 2];
                    file.ReadExactly(body);
                    if (body.AsSpan().StartsWith(PhotoshopSignature)) photoshop.AddRange(body.AsSpan(PhotoshopSignature.Length));
                }
                at += 2 + length;
            }
            return photoshop.Count == 0 ? null : StaleIptcCaption(photoshop.ToArray());
        }
        catch (EndOfStreamException)
        {
            // The file got shorter while it was read.
            return null;
        }
    }

    /// <summary>The caption's readings (UTF-8, Latin-1) from Photoshop's joined resource list, when its digest is stale.</summary>
    private static string[]? StaleIptcCaption(byte[] b)
    {
        byte[]? iptc = null, digest = null;
        var p = 0;
        while (p + 12 <= b.Length && ResourceSignatures.Any(sig => b.AsSpan(p, 4).SequenceEqual(sig)))
        {
            var id = (b[p + 4] << 8) | b[p + 5];
            // A Pascal name, padded to an even length with its length byte.
            var q = p + 6 + ((b[p + 6] + 2) & ~1);
            if (q + 4 > b.Length) break;
            var size = (long)(uint)((b[q] << 24) | (b[q + 1] << 16) | (b[q + 2] << 8) | b[q + 3]);
            q += 4;
            // Compared without adding, so a huge size can't wrap round and pass.
            if (size > b.Length - q) break;
            if (id == 0x0404) iptc ??= b[q..(q + (int)size)];
            if (id == 0x0425) digest ??= b[q..(q + (int)size)];
            p = q + (int)size + (int)(size & 1);
        }
        // Any digest that isn't the IPTC's MD5 (a damaged one too) means the IPTC changed since.
        if (iptc is null || digest is null || System.Security.Cryptography.MD5.HashData(iptc).AsSpan().SequenceEqual(digest)) return null;
        // Datasets: 0x1C, record, dataset, a 2-byte length (an extended length isn't followed).
        for (var i = 0; i + 5 <= iptc.Length && iptc[i] == 0x1C; )
        {
            int record = iptc[i + 1], dataset = iptc[i + 2], length = (iptc[i + 3] << 8) | iptc[i + 4];
            if ((length & 0x8000) != 0 || i + 5 + length > iptc.Length) return null;
            if (record == 2 && dataset == 120) return [.. XmpPacket.TextOf(iptc, i + 5, length)];
            i += 5 + length;
        }
        return null;
    }

    // MARK: Extended XMP

    private const string DontAddUp = "Its metadata is spread over several parts (Extended XMP) that don't add up.";

    /// <summary>The Extended XMP parts, checked as XMP's specification describes them: each is the GUID (32 hex digits),
    /// the whole extension's length and this part's offset in it (4 bytes each, big-endian), then its bytes. Together
    /// they must cover the extension exactly, its MD5 must be the GUID, and the main packet must name it. Then none of the
    /// fields being written may be in it.</summary>
    /// <returns>The GUID.</returns>
    private static string CheckExtension(byte[] jpeg, List<Segment> parts, XDocument? main, MetadataChanges changes)
    {
        // Exactly 32 uppercase hex digits, as XMP writes an MD5 and the Mac requires; each part names it byte for byte.
        var guid = main is null ? null : XmpPacket.ExtendedGuid(main);
        if (guid is not { Length: 32 } || !guid.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')) throw new MetadataRefusedException(DontAddUp);
        long total = -1;
        var pieces = new List<(long Offset, byte[] Bytes)>();
        foreach (var s in parts)
        {
            var at = s.Offset + 4 + ExtendedSignature.Length;
            // The header and at least one byte: an empty part isn't one.
            if (at + 40 >= s.End) throw new MetadataRefusedException(DontAddUp);
            var name = Encoding.ASCII.GetString(jpeg, at, 32);
            long length = (uint)((jpeg[at + 32] << 24) | (jpeg[at + 33] << 16) | (jpeg[at + 34] << 8) | jpeg[at + 35]);
            long offset = (uint)((jpeg[at + 36] << 24) | (jpeg[at + 37] << 16) | (jpeg[at + 38] << 8) | jpeg[at + 39]);
            if (!name.Equals(guid, StringComparison.Ordinal) || (total >= 0 && total != length)) throw new MetadataRefusedException(DontAddUp);
            total = length;
            pieces.Add((offset, jpeg[(at + 40)..s.End]));
        }
        // The parts can only add up to what they hold: a larger length is refused before anything is allocated for it.
        if (total != pieces.Sum(p => (long)p.Bytes.Length)) throw new MetadataRefusedException(DontAddUp);
        var whole = new byte[total];
        long next = 0;
        foreach (var (offset, bytes) in pieces.OrderBy(p => p.Offset))
        {
            // In order, with no gap or overlap. (With the sum checked above this also keeps every part within the total.)
            if (offset != next || offset + bytes.Length > total) throw new MetadataRefusedException(DontAddUp);
            bytes.CopyTo(whole, offset);
            next += bytes.Length;
        }
        if (next != total) throw new MetadataRefusedException(DontAddUp);
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(whole));
        if (!md5.Equals(guid, StringComparison.Ordinal)) throw new MetadataRefusedException(DontAddUp);
        var extended = XmpPacket.Parse(whole);
        // Named, so it's clear what can't be changed: "its caption is kept in another part".
        if (XmpPacket.Written(changes).FirstOrDefault(n => XmpPacket.Holds(extended, n)) is { } held)
            throw new MetadataRefusedException($"Its metadata is spread over several parts (Extended XMP), and its {FieldName(held)} is kept in another part.");
        return guid;
    }

    private static string FieldName(System.Xml.Linq.XName name) => name.LocalName switch
    {
        "description" => "caption",
        "title" => "title",
        "AltTextAccessibility" => "alt text",
        "ExtDescrAccessibility" => "extended description",
        _ => "rating",
    };

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
}
