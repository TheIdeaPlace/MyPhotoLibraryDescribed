using System.IO.Compression;
using System.Text;

namespace PhotoFinder.Services;

/// <summary>
/// Changes a PNG's caption and accessibility fields by editing its chunks (#82), as the Mac does since #61. The XMP (the
/// iTXt chunk "XML:com.adobe.xmp") is read, changed and written back as one uncompressed chunk, where it was or just
/// before the picture; every other chunk, and anything after IEND, is copied byte for byte, the picture included.
///
/// - Windows keeps a PNG's Title in the same XMP (dc:title), so it follows the caption there.
/// - When the caption changes, the eXIf ImageDescription is emptied in place, as the Mac does: every macOS and iOS
///   screenshot carries "Screenshot" there, which Apple's ImageIO reads ahead of the XMP. An XPTitle holding the old
///   caption is emptied too.
/// - Anything that doesn't parse (a damaged chunk, XMP compressed in an unknown way or that isn't XML) is refused, so
///   the file is left alone.
/// </summary>
public static class PngMetadata
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] XmpKeyword = Encoding.ASCII.GetBytes("XML:com.adobe.xmp");

    /// <summary>A chunk as it is in the file: its offset, total length (length, type, data and CRC) and type.</summary>
    private readonly record struct Chunk(int Offset, int Length, string Type)
    {
        public int Data => Offset + 8;
        public int DataLength => Length - 12;
        public int End => Offset + Length;
    }

    public static bool LooksLikePng(byte[] b) => b.Length >= 8 && b.AsSpan(0, 8).SequenceEqual(Signature);

    /// <summary>The caption, alt text and extended description in the PNG's XMP, null for each it doesn't have. Throws
    /// <see cref="MetadataRefusedException"/> when the file or its XMP can't be read.</summary>
    public static (string? Caption, string? AltText, string? ExtendedDescription) Fields(byte[] png)
    {
        var (chunks, _) = Chunks(png);
        var xmp = chunks.FirstOrDefault(c => IsXmp(png, c));
        return xmp.Length == 0 ? (null, null, null) : XmpPacket.Fields(XmpPacket.Parse(Packet(png, xmp)));
    }

    /// <summary>The PNG with the changes made. Throws <see cref="MetadataRefusedException"/> for a file it can't safely
    /// change.</summary>
    public static byte[] Apply(byte[] png, MetadataChanges changes)
    {
        var (chunks, end) = Chunks(png);
        var xmpChunks = chunks.Where(c => IsXmp(png, c)).ToList();
        // The picture's first chunk: IDAT, or an animated PNG's acTL or fcTL before it.
        var firstIdat = chunks.FindIndex(c => c.Type is "IDAT" or "acTL" or "fcTL");
        var document = xmpChunks.Count > 0 ? XmpPacket.Parse(Packet(png, xmpChunks[0])) : XmpPacket.New();
        XmpPacket.Edit(document, changes);
        var packet = XmpPacket.Serialize(document, padded: false);
        // Keyword, its terminator, uncompressed, compression method, no language, no translated keyword.
        var xmpChunk = Build("iTXt", [.. XmpKeyword, 0, 0, 0, 0, 0, .. packet]);
        // Where the old XMP was when it came before the picture, otherwise just before it, where readers look first.
        var oldAt = xmpChunks.Count > 0 ? chunks.IndexOf(xmpChunks[0]) : -1;
        var insertAt = oldAt >= 0 && oldAt < firstIdat ? oldAt : firstIdat;

        var output = new MemoryStream(png.Length + xmpChunk.Length);
        output.Write(Signature);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (i == insertAt) output.Write(xmpChunk);
            if (xmpChunks.Contains(c)) continue;
            if (c.Type == "eXIf" && (changes.ClearOwnTitle is { Length: > 0 } || changes.Stars is not null || changes.Caption is not null))
            {
                var data = png[c.Data..(c.Data + c.DataLength)];
                if (XmpPacket.PatchExif(data, 0, data.Length, changes, clearDescription: changes.Caption is not null))
                {
                    output.Write(Build("eXIf", data));
                    continue;
                }
            }
            output.Write(png.AsSpan(c.Offset, c.Length));
        }
        // Whatever followed IEND, as it was.
        output.Write(png.AsSpan(end));
        return output.ToArray();
    }

    // MARK: Chunks

    /// <summary>The chunks up to and including IEND, and where IEND ends. Throws when the file can't be followed.</summary>
    private static (List<Chunk> Chunks, int End) Chunks(byte[] b)
    {
        if (!LooksLikePng(b)) throw new MetadataRefusedException("It isn't a PNG that can be followed.");
        var list = new List<Chunk>();
        var at = 8;
        while (true)
        {
            if (at + 12 > b.Length) throw new MetadataRefusedException("The file is cut short.");
            var length = (long)((uint)(b[at] << 24) | (uint)(b[at + 1] << 16) | (uint)(b[at + 2] << 8) | b[at + 3]);
            if (length > int.MaxValue || at + 12 + length > b.Length) throw new MetadataRefusedException("The file is cut short.");
            var type = b.AsSpan(at + 4, 4);
            foreach (var t in type)
                if (t is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
                    throw new MetadataRefusedException("Part of the file is damaged.");
            var chunk = new Chunk(at, (int)length + 12, Encoding.ASCII.GetString(type));
            var stored = ReadUInt32(b, chunk.End - 4);
            if (stored != Crc32(b.AsSpan(at + 4, (int)length + 4))) throw new MetadataRefusedException("Part of the file is damaged.");
            if (list.Count == 0 && chunk.Type != "IHDR") throw new MetadataRefusedException("The file doesn't start with its header.");
            list.Add(chunk);
            at = chunk.End;
            if (chunk.Type == "IEND") break;
        }
        if (!list.Any(c => c.Type == "IDAT")) throw new MetadataRefusedException("The file has no picture data.");
        return (list, at);
    }

    private static bool IsXmp(byte[] b, Chunk c) =>
        c.Type == "iTXt" && c.DataLength > XmpKeyword.Length && b.AsSpan(c.Data, XmpKeyword.Length).SequenceEqual(XmpKeyword)
        && b[c.Data + XmpKeyword.Length] == 0;

    /// <summary>The XMP text of an iTXt XMP chunk, inflated when it's compressed.</summary>
    private static byte[] Packet(byte[] b, Chunk c) => ITxtText(b, c, XmpKeyword.Length + 1);

    /// <summary>An iTXt chunk's text, from just after its keyword's terminator.</summary>
    private static byte[] ITxtText(byte[] b, Chunk c, int start)
    {
        var data = b.AsSpan(c.Data, c.DataLength);
        var at = start;
        if (at + 2 > data.Length) throw new MetadataRefusedException("Its text chunk is cut short.");
        var (compressed, method) = (data[at], data[at + 1]);
        at += 2;
        // The language tag and the translated keyword, each ending in a zero.
        for (var k = 0; k < 2; k++)
        {
            var zero = data[at..].IndexOf((byte)0);
            if (zero < 0) throw new MetadataRefusedException("Its text chunk is cut short.");
            at += zero + 1;
        }
        var text = data[at..].ToArray();
        return (compressed, method) switch
        {
            (0, _) => text,
            (1, 0) => Inflate(text),
            _ => throw new MetadataRefusedException("Its metadata is compressed in an unknown way."),
        };
    }

    /// <summary>More than any real XMP: a packet inflating past this is refused rather than filling memory.</summary>
    internal const int MaxInflated = 16 * 1024 * 1024;

    private static byte[] Inflate(byte[] input)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxInflated) throw new MetadataRefusedException("Its metadata is too large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new MetadataRefusedException("Its compressed metadata is damaged.");
        }
    }

    // MARK: Bytes

    private static byte[] Build(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        WriteUInt32(chunk, 0, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4, 4));
        data.CopyTo(chunk, 8);
        WriteUInt32(chunk, 8 + data.Length, Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint ReadUInt32(byte[] b, int at) =>
        (uint)(b[at] << 24) | (uint)(b[at + 1] << 16) | (uint)(b[at + 2] << 8) | b[at + 3];

    private static void WriteUInt32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>PNG's CRC-32, over a chunk's type and data.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in bytes) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
