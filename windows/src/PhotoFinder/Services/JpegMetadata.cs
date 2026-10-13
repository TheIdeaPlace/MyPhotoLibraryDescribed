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
/// - Extended XMP (spread over several segments) isn't edited.
/// </summary>
public static class JpegMetadata
{
    private static readonly byte[] XmpSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] ExtendedSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    private static readonly byte[] ExifSignature = Encoding.ASCII.GetBytes("Exif\0\0");
    private static readonly byte[] MpfSignature = Encoding.ASCII.GetBytes("MPF\0");

    /// <summary>A segment's largest length field: the length counts itself, not the marker.</summary>
    private const int MaxSegmentLength = 65535;

    private readonly record struct Segment(byte Marker, int Offset, int Length)
    {
        public int End => Offset + Length;
    }

    /// <summary>Whether the bytes start like a JPEG.</summary>
    public static bool LooksLikeJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>The JPEG with the changes made. Throws <see cref="MetadataRefusedException"/> for a file it can't safely
    /// change (not a JPEG it can follow, unreadable XMP, Extended XMP, too much metadata) and
    /// <see cref="SecondPictureException"/> for a multi-picture file whose index can't be kept right.</summary>
    public static byte[] Apply(byte[] jpeg, MetadataChanges changes)
    {
        var segments = Segments(jpeg);
        var xmpAt = segments.FindIndex(s => IsApp1With(jpeg, s, XmpSignature));
        if (segments.Any(s => IsApp1With(jpeg, s, ExtendedSignature)))
            throw new MetadataRefusedException("Its metadata is spread over several parts (Extended XMP), which isn't changed.");
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
        XmpPacket.Edit(document, changes);
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
