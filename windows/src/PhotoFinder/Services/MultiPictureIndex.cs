namespace PhotoFinder.Services;

/// <summary>
/// A JPEG's Multi-Picture index (CIPA DC-007, the APP2 "MPF" segment) of an Ultra HDR photo's gain map or an MPO's other
/// view (#90). It's a small TIFF structure: an MP Entry for each picture, with its size and where it starts, counted from
/// the index's own header (the first picture starts at 0, the file's start). Changing the XMP before the pictures that
/// follow changes the first picture's size, and, when the XMP comes after the index, where the others start.
/// </summary>
internal sealed class MultiPictureIndex
{
    private const ushort MpEntryTag = 0xB002;

    private readonly bool _little;
    private readonly int _header;
    private readonly int _entries;
    private readonly int _count;
    private readonly uint[] _sizes;
    private readonly uint[] _offsets;

    private MultiPictureIndex(bool little, int header, int entries, int count, uint[] sizes, uint[] offsets)
    {
        (_little, _header, _entries, _count, _sizes, _offsets) = (little, header, entries, count, sizes, offsets);
    }

    /// <summary>The index whose header (the byte order mark) is at <paramref name="header"/>, checked against the file:
    /// every picture after the first starts with a JPEG's start marker and fits in the file. Throws
    /// <see cref="SecondPictureException"/> otherwise, as the change couldn't be made without breaking it.</summary>
    public static MultiPictureIndex Read(byte[] jpeg, int header, int segmentEnd)
    {
        bool little;
        if (Has(jpeg, header, "II*\0"u8)) little = true;
        else if (Has(jpeg, header, "MM\0*"u8)) little = false;
        else throw new SecondPictureException();
        uint U32(int at) => at + 4 <= segmentEnd ? Read32(jpeg, at, little) : throw new SecondPictureException();
        int U16(int at) => at + 2 <= segmentEnd ? Read16(jpeg, at, little) : throw new SecondPictureException();

        var ifd = header + (long)U32(header + 4);
        if (ifd + 2 > segmentEnd) throw new SecondPictureException();
        var tags = U16((int)ifd);
        for (var t = 0; t < tags; t++)
        {
            var tag = (int)ifd + 2 + t * 12;
            if (U16(tag) != MpEntryTag) continue;
            // UNDEFINED, 16 bytes a picture, at an offset from the header.
            var bytes = U32(tag + 4);
            if (U16(tag + 2) != 7 || bytes == 0 || bytes % 16 != 0 || bytes > 16 * 64) throw new SecondPictureException();
            var count = (int)(bytes / 16);
            var entries = header + (long)U32(tag + 8);
            if (entries + bytes > segmentEnd) throw new SecondPictureException();
            var sizes = new uint[count];
            var offsets = new uint[count];
            for (var i = 0; i < count; i++)
            {
                sizes[i] = U32((int)entries + i * 16 + 4);
                offsets[i] = U32((int)entries + i * 16 + 8);
            }
            var index = new MultiPictureIndex(little, header, (int)entries, count, sizes, offsets);
            index.Check(jpeg, header, sizes, offsets);
            return index;
        }
        throw new SecondPictureException();
    }

    /// <summary>Patches the index in <paramref name="changed"/>, the file after its header grew by
    /// <paramref name="delta"/> bytes, then checks it still points at the pictures.</summary>
    /// <param name="movesWithChange">Whether the change came before the index, so the index moved along with the
    /// pictures after it.</param>
    public void Patch(byte[] changed, int delta, bool movesWithChange)
    {
        var shift = movesWithChange ? delta : 0;
        var header = _header + shift;
        var entries = _entries + shift;
        var sizes = (uint[])_sizes.Clone();
        var offsets = (uint[])_offsets.Clone();
        sizes[0] = checked((uint)(sizes[0] + delta));
        if (!movesWithChange)
            for (var i = 1; i < _count; i++) offsets[i] = checked((uint)(offsets[i] + delta));
        for (var i = 0; i < _count; i++)
        {
            Write32(changed, entries + i * 16 + 4, sizes[i], _little);
            Write32(changed, entries + i * 16 + 8, offsets[i], _little);
        }
        Check(changed, header, sizes, offsets);
    }

    /// <summary>Where the other pictures start is what readers use to find them, so that must be exact. The first
    /// picture's size needn't be: Apple's HDR photos record it a little long, running into the gain map, so it's only
    /// kept in step (and within the file).</summary>
    private void Check(byte[] jpeg, int header, uint[] sizes, uint[] offsets)
    {
        if (offsets[0] != 0 || sizes[0] <= header || sizes[0] > jpeg.Length) throw new SecondPictureException();
        for (var i = 1; i < _count; i++)
        {
            var start = header + (long)offsets[i];
            if (offsets[i] == 0 || start + sizes[i] > jpeg.Length || !Has(jpeg, (int)start, [0xFF, 0xD8, 0xFF]))
                throw new SecondPictureException();
        }
    }

    private static bool Has(byte[] b, int at, ReadOnlySpan<byte> signature) =>
        at >= 0 && at + signature.Length <= b.Length && b.AsSpan(at, signature.Length).SequenceEqual(signature);

    private static int Read16(byte[] b, int at, bool little) =>
        little ? b[at] | (b[at + 1] << 8) : (b[at] << 8) | b[at + 1];

    private static uint Read32(byte[] b, int at, bool little) =>
        little ? (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24))
               : (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    private static void Write32(byte[] b, int at, uint value, bool little)
    {
        for (var i = 0; i < 4; i++) b[at + i] = (byte)(value >> (little ? 8 * i : 8 * (3 - i)));
    }
}
