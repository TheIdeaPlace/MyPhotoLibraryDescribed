using System.Buffers.Binary;
using System.Text;

namespace PhotoFinder.Services;

/// <summary>
/// Changes a HEIC (HEIF) file's XMP by editing its boxes (#31), as the Mac does through ImageIO. The photo's XMP is an
/// item of its own (a 'mime' item, application/rdf+xml) that describes the main picture ('cdsc' in 'iref'); 'iloc' says
/// where its bytes are. An iPhone's HEIC has an XMP item already, but it describes the HDR gain map, not the photo, so
/// it's left alone; Windows' own property handler adds one for the photo the first time a Title is saved.
///
/// - With an XMP item for the photo, the new XMP goes where the old one was when it fits (the packet keeps its padding),
///   so the file keeps its size; otherwise it goes in a new 'mdat' box at the end, only that item's place and length in
///   'iloc' are patched, and the old bytes are blanked, so an old caption doesn't linger.
/// - Without one, an item is added: an entry in 'iinf', a 'cdsc' reference to the photo (and to anything the EXIF item
///   describes alongside it, such as the gain map's tone map, as Windows does), and a place in 'iloc', with the XMP in a
///   new 'mdat' box at the end. The meta box grows, so every item stored after it has its place moved by that much.
/// - The pictures, the EXIF item, the gain map and depth map stay byte for byte. Anything it can't follow (an image
///   sequence, an XMP stored inside the meta box, compressed or in pieces, a box it can't read) is refused with
///   <see cref="MetadataRefusedException"/>, before anything is written.
/// </summary>
public static class HeifMetadata
{
    private static readonly HashSet<string> Brands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "mif2"];
    private const string XmpType = "application/rdf+xml";

    /// <summary>Whether the bytes start like a HEIF file: an 'ftyp' box naming a HEIF brand.</summary>
    public static bool LooksLikeHeif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 16 || !b.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), (uint)b.Length);
        for (var at = 8; at + 4 <= size; at += 4)
            if (at != 12 && Brands.Contains(Encoding.ASCII.GetString(b.Slice(at, 4)))) return true;
        return false;
    }

    /// <summary>The caption and accessibility fields from the photo's XMP item, reading only the meta box and the item;
    /// nulls when it has none.</summary>
    public static (string? Caption, string? AltText, string? ExtendedDescription) Fields(Stream file)
    {
        var head = new byte[Math.Min(file.Length, 64)];
        file.Position = 0;
        file.ReadExactly(head);
        if (!LooksLikeHeif(head)) throw Unfollowable();
        var top = TopBoxes(file);
        var metaBox = top.FirstOrDefault(x => x.Type == "meta");
        if (metaBox.Type is null) throw Unfollowable();
        if (metaBox.Size > 16 << 20) throw new MetadataRefusedException("Its metadata is too large to follow.");
        var bytes = new byte[metaBox.Size];
        file.Position = metaBox.At;
        file.ReadExactly(bytes);
        var meta = Meta.Parse(bytes, metaBox.At, file.Length);
        if (meta.PhotoXmp() is not { } item) return (null, null, null);
        var (at, length) = meta.Place(item);
        if (length > 4 << 20) throw new MetadataRefusedException("Its XMP is too large to follow.");
        var packet = new byte[length];
        file.Position = at;
        file.ReadExactly(packet);
        return XmpPacket.Fields(XmpPacket.Parse(packet));
    }

    /// <summary>The file with the changes made to the photo's XMP. Throws <see cref="MetadataRefusedException"/> for a
    /// file it can't safely change.</summary>
    public static byte[] Apply(byte[] heif, MetadataChanges changes)
    {
        if (!LooksLikeHeif(heif)) throw Unfollowable();
        using var stream = new MemoryStream(heif, writable: false);
        var top = TopBoxes(stream);
        if (top.Any(x => x.Type == "moov")) throw new MetadataRefusedException("It's a sequence of pictures, which isn't changed.");
        if (top[^1].RunsToEnd) throw new MetadataRefusedException("Its last part runs to the end of the file, so nothing can be added after it.");
        var metaBox = top.FirstOrDefault(x => x.Type == "meta");
        if (metaBox.Type is null || metaBox.LargeSize) throw Unfollowable();
        var meta = Meta.Parse(heif.AsSpan((int)metaBox.At, (int)metaBox.Size), metaBox.At, heif.Length);

        if (meta.PhotoXmp() is { } item)
        {
            var (at, length) = meta.Place(item);
            var document = XmpPacket.Parse(heif.AsSpan((int)at, (int)length));
            XmpPacket.Edit(document, changes);
            var packet = XmpPacket.Serialize(document, (int)length);
            if (packet.Length == length)
            {
                var same = (byte[])heif.Clone();
                packet.CopyTo(same, (int)at);
                return same;
            }
            // At the end, in a box of its own; only this item's offset and length change, in place.
            var start = (long)heif.Length + 8;
            var moved = meta.Moved(item, start, packet.Length);
            var result = new byte[heif.Length + 8 + packet.Length];
            heif.CopyTo(result, 0);
            moved.CopyTo(result, (int)metaBox.At);
            WriteMdat(result, heif.Length, packet);
            // The old XMP, now unused, as XMP padding: spaces.
            result.AsSpan((int)at, (int)length).Fill((byte)' ');
            return result;
        }

        // No XMP for the photo yet: one is added, with room to grow.
        var fresh = XmpPacket.New();
        XmpPacket.Edit(fresh, changes);
        var xmp = XmpPacket.Serialize(fresh, 1);
        var grown = meta.WithXmpItem(heif.Length, xmp.Length);
        var delta = grown.Length - (int)metaBox.Size;
        var output = new byte[heif.Length + delta + 8 + xmp.Length];
        heif.AsSpan(0, (int)metaBox.At).CopyTo(output);
        grown.CopyTo(output, (int)metaBox.At);
        heif.AsSpan((int)metaBox.End).CopyTo(output.AsSpan((int)metaBox.At + grown.Length));
        WriteMdat(output, heif.Length + delta, xmp);
        return output;
    }

    private static void WriteMdat(byte[] b, int at, byte[] payload)
    {
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), (uint)(8 + payload.Length));
        "mdat"u8.CopyTo(b.AsSpan(at + 4));
        payload.CopyTo(b, at + 8);
    }

    // MARK: Boxes

    private readonly record struct TopBox(string Type, long At, long Size, bool LargeSize, bool RunsToEnd)
    {
        public long End => At + Size;
    }

    /// <summary>The file's top-level boxes, which must cover it exactly.</summary>
    private static List<TopBox> TopBoxes(Stream file)
    {
        var list = new List<TopBox>();
        var header = new byte[16];
        long at = 0;
        while (at < file.Length)
        {
            if (at + 8 > file.Length) throw Unfollowable();
            file.Position = at;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var large = false;
            var toEnd = false;
            if (size == 1)
            {
                if (at + 16 > file.Length) throw Unfollowable();
                file.ReadExactly(header, 8, 8);
                var big = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (big > long.MaxValue) throw Unfollowable();
                size = (long)big;
                large = true;
            }
            else if (size == 0)
            {
                size = file.Length - at;
                toEnd = true;
            }
            if (size < 8 || at + size > file.Length) throw Unfollowable();
            list.Add(new TopBox(Encoding.ASCII.GetString(header, 4, 4), at, size, large, toEnd));
            at += size;
        }
        if (list.Count == 0) throw Unfollowable();
        return list;
    }

    /// <summary>A box inside another: its type, where it starts, where its contents start, and where it ends.</summary>
    private readonly record struct Box(string Type, int At, int Body, int End);

    private static List<Box> Children(ReadOnlySpan<byte> b, int start, int end)
    {
        var list = new List<Box>();
        var at = start;
        while (at < end)
        {
            if (at + 8 > end) throw Unfollowable();
            long size = BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
            // Boxes inside the meta box are small: a large or open-ended size there isn't followed.
            if (size < 8 || at + size > end) throw Unfollowable();
            list.Add(new Box(Encoding.ASCII.GetString(b.Slice(at + 4, 4)), at, at + 8, (int)(at + size)));
            at += (int)size;
        }
        return list;
    }

    /// <summary>An 'iloc' entry: where an item's bytes are.</summary>
    private sealed record Location(uint Id, int Method, int Reference, ulong Base, List<(ulong Index, ulong Offset, ulong Length)> Extents);

    /// <summary>The meta box, read: the boxes it holds, the photo's item, the item types and descriptions ('iinf',
    /// 'iref') and places ('iloc').</summary>
    private sealed class Meta
    {
        private byte[] _bytes = [];
        private long _at;
        private long _fileLength;
        private int _version;
        private List<Box> _boxes = [];
        private uint _primary;
        private readonly Dictionary<uint, (string Type, string ContentType, string Encoding)> _items = [];
        private readonly List<(string Type, uint From, List<uint> To)> _references = [];
        private int _ilocVersion, _offsetSize, _lengthSize, _baseSize, _indexSize;
        private readonly List<Location> _locations = [];
        private int _referenceWidth;

        public static Meta Parse(ReadOnlySpan<byte> bytes, long at, long fileLength)
        {
            var meta = new Meta { _bytes = bytes.ToArray(), _at = at, _fileLength = fileLength };
            var outer = Children(bytes, 0, bytes.Length);
            if (outer.Count != 1 || outer[0].Type != "meta" || outer[0].Body + 4 > outer[0].End) throw Unfollowable();
            meta._version = bytes[outer[0].Body];
            meta._boxes = Children(bytes, outer[0].Body + 4, outer[0].End);
            meta.ReadPrimary(bytes);
            meta.ReadItems(bytes);
            meta.ReadReferences(bytes);
            meta.ReadLocations(bytes);
            return meta;
        }

        private Box Find(string type) =>
            _boxes.Count(x => x.Type == type) == 1 ? _boxes.Single(x => x.Type == type) : throw Unfollowable();

        private void ReadPrimary(ReadOnlySpan<byte> b)
        {
            var pitm = Find("pitm");
            var p = new Reader(b, pitm.Body, pitm.End);
            var version = p.U8();
            p.Skip(3);
            _primary = version == 0 ? p.U16() : p.U32();
        }

        private void ReadItems(ReadOnlySpan<byte> b)
        {
            var iinf = Find("iinf");
            var p = new Reader(b, iinf.Body, iinf.End);
            var version = p.U8();
            p.Skip(3);
            var count = version == 0 ? p.U16() : p.U32();
            var entries = Children(b, p.At, iinf.End);
            if (entries.Count != count) throw Unfollowable();
            foreach (var infe in entries)
            {
                if (infe.Type != "infe") throw Unfollowable();
                var e = new Reader(b, infe.Body, infe.End);
                var v = e.U8();
                e.Skip(3);
                if (v < 2) throw Unfollowable();
                var id = v == 2 ? e.U16() : e.U32();
                e.Skip(2);
                var type = Encoding.ASCII.GetString(e.Bytes(4));
                var strings = Encoding.UTF8.GetString(b[e.At..infe.End]).Split('\0');
                if (!_items.TryAdd(id, (type, type == "mime" && strings.Length > 1 ? strings[1].Trim() : "",
                        type == "mime" && strings.Length > 2 ? strings[2].Trim() : "")))
                    throw Unfollowable();
            }
            if (!_items.ContainsKey(_primary)) throw Unfollowable();
        }

        private void ReadReferences(ReadOnlySpan<byte> b)
        {
            var irefs = _boxes.Where(x => x.Type == "iref").ToList();
            if (irefs.Count > 1) throw Unfollowable();
            if (irefs.Count == 0) { _referenceWidth = 2; return; }
            var iref = irefs[0];
            var p = new Reader(b, iref.Body, iref.End);
            var version = p.U8();
            p.Skip(3);
            _referenceWidth = version == 0 ? 2 : 4;
            foreach (var r in Children(b, p.At, iref.End))
            {
                var e = new Reader(b, r.Body, r.End);
                var from = _referenceWidth == 2 ? e.U16() : e.U32();
                var count = e.U16();
                var to = new List<uint>();
                for (var i = 0; i < count; i++) to.Add(_referenceWidth == 2 ? e.U16() : e.U32());
                _references.Add((r.Type, from, to));
            }
        }

        private void ReadLocations(ReadOnlySpan<byte> b)
        {
            var iloc = Find("iloc");
            var p = new Reader(b, iloc.Body, iloc.End);
            _ilocVersion = p.U8();
            if (_ilocVersion > 2) throw Unfollowable();
            p.Skip(3);
            var sizes = p.U8();
            var more = p.U8();
            (_offsetSize, _lengthSize, _baseSize) = (sizes >> 4, sizes & 15, more >> 4);
            _indexSize = _ilocVersion is 1 or 2 ? more & 15 : 0;
            if (!Valid(_offsetSize) || !Valid(_lengthSize) || !Valid(_baseSize) || !Valid(_indexSize)) throw Unfollowable();
            var count = _ilocVersion < 2 ? p.U16() : p.U32();
            for (var i = 0; i < count; i++)
            {
                var id = _ilocVersion < 2 ? p.U16() : p.U32();
                var method = _ilocVersion is 1 or 2 ? (int)(p.U16() & 15) : 0;
                var reference = (int)p.U16();
                var baseOffset = p.UInt(_baseSize);
                var extents = new List<(ulong, ulong, ulong)>();
                var n = p.U16();
                for (var e = 0; e < n; e++) extents.Add((p.UInt(_indexSize), p.UInt(_offsetSize), p.UInt(_lengthSize)));
                _locations.Add(new Location(id, method, reference, baseOffset, extents));
            }
            if (p.At != iloc.End) throw Unfollowable();
        }

        /// <summary>The XMP item describing the photo, if there is one.</summary>
        public uint? PhotoXmp()
        {
            var xmp = _references.Where(r => r.Type == "cdsc" && r.To.Contains(_primary) && IsXmp(r.From)).Select(r => r.From).Distinct().ToList();
            return xmp.Count switch
            {
                0 => null,
                1 => xmp[0],
                _ => throw new MetadataRefusedException("It has more than one XMP for the photo, which isn't changed."),
            };
        }

        private bool IsXmp(uint id) =>
            _items.TryGetValue(id, out var item) && item.Type == "mime" && item.ContentType.Equals(XmpType, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where an XMP item's bytes are in the file: one extent, in this file, outside the meta box.</summary>
        public (long At, long Length) Place(uint id)
        {
            if (_items[id].Encoding.Length > 0) throw new MetadataRefusedException("Its XMP is compressed, which isn't changed.");
            var location = _locations.Where(l => l.Id == id).ToList();
            if (location.Count != 1) throw Unfollowable();
            var l = location[0];
            if (l.Method != 0 || l.Reference != 0 || l.Extents.Count != 1 || _offsetSize == 0 || _lengthSize == 0)
                throw new MetadataRefusedException("Its XMP is stored in a way that isn't changed.");
            if (l.Base > long.MaxValue / 2 || l.Extents[0].Offset > long.MaxValue / 2 || l.Extents[0].Length > long.MaxValue / 2) throw Unfollowable();
            var at = (long)(l.Base + l.Extents[0].Offset);
            var length = (long)l.Extents[0].Length;
            if (length <= 0 || at + length > _fileLength || (at < _at + _bytes.Length && _at < at + length)) throw Unfollowable();
            // Its bytes are its own, so blanking them when it moves touches nothing else.
            foreach (var other in _locations.Where(o => o.Id != id && o.Method == 0 && o.Reference == 0))
                foreach (var e in other.Extents)
                {
                    var from = (long)(other.Base + e.Offset);
                    if (from < at + length && at < from + (long)e.Length) throw Unfollowable();
                }
            return (at, length);
        }

        /// <summary>The meta box's bytes with item <paramref name="id"/> now at <paramref name="at"/>, length
        /// <paramref name="length"/>: the same size, only those two fields changed.</summary>
        public byte[] Moved(uint id, long at, long length)
        {
            var l = _locations.Single(x => x.Id == id);
            var offset = at - (long)l.Base;
            if (!Fits(offset, _offsetSize) || !Fits(length, _lengthSize)) throw new MetadataRefusedException("It's too large for its XMP to be moved.");
            var locations = _locations.Select(x => x == l ? x with { Extents = [(x.Extents[0].Index, (ulong)offset, (ulong)length)] } : x).ToList();
            var bytes = Rebuilt(locations, addedItem: null, added: null);
            if (bytes.Length != _bytes.Length) throw Unfollowable();
            return bytes;
        }

        /// <summary>The meta box's bytes with an XMP item added for the photo, its bytes to go at the end of a file now
        /// <paramref name="fileLength"/> long, in a new 'mdat' box; every item after the meta box moved by its growth.</summary>
        public byte[] WithXmpItem(long fileLength, int xmpLength)
        {
            if (_offsetSize == 0 || _lengthSize == 0) throw new MetadataRefusedException("Its layout has no room to add an XMP.");
            var id = _items.Keys.Max() + 1;
            if (id > ushort.MaxValue && (_ilocVersion < 2 || _referenceWidth == 2)) throw new MetadataRefusedException("It has too many parts to add an XMP.");
            // As Windows does: the XMP describes what the EXIF item describes (the photo, and its gain map's tone map),
            // or the photo alone.
            var exif = _references.FirstOrDefault(r => r.Type == "cdsc" && r.To.Contains(_primary) && _items.TryGetValue(r.From, out var i) && i.Type == "Exif");
            var describes = exif.To is { Count: > 0 } ? exif.To : [_primary];
            var infe = Infe(id);
            var reference = Reference(id, describes);

            // Built once to learn its size, then with every place after the meta box moved by the growth.
            var draft = Rebuilt(_locations, (id, 0, 0), (infe, reference));
            var delta = (ulong)(draft.Length - _bytes.Length);
            var end = _at + _bytes.Length;
            var moved = new List<Location>();
            foreach (var l in _locations)
            {
                if (l.Method != 0 || l.Reference != 0) { moved.Add(l); continue; }
                // A base offset past the meta box moves; otherwise each extent past it does.
                var baseMoves = _baseSize > 0 && (long)l.Base >= end;
                var baseOffset = baseMoves ? l.Base + delta : l.Base;
                if (!Fits((long)baseOffset, _baseSize)) throw new MetadataRefusedException("It's too large to add an XMP.");
                var extents = new List<(ulong, ulong, ulong)>();
                foreach (var e in l.Extents)
                {
                    var abs = (long)(l.Base + e.Offset);
                    // Something stored inside the meta box itself would move in a way this doesn't follow.
                    if (abs >= _at && abs < end) throw Unfollowable();
                    var offset = !baseMoves && abs >= end ? e.Offset + delta : e.Offset;
                    if (!Fits((long)offset, _offsetSize)) throw new MetadataRefusedException("It's too large to add an XMP.");
                    extents.Add((e.Index, offset, e.Length));
                }
                moved.Add(l with { Base = baseOffset, Extents = extents });
            }
            var xmpAt = fileLength + (long)delta + 8;
            if (!Fits(xmpAt, _offsetSize) || !Fits(xmpLength, _lengthSize)) throw new MetadataRefusedException("It's too large to add an XMP.");
            var bytes = Rebuilt(moved, (id, (ulong)xmpAt, (ulong)xmpLength), (infe, reference));
            if (bytes.Length != draft.Length) throw Unfollowable();
            return bytes;
        }

        private static byte[] Infe(uint id)
        {
            var v3 = id > ushort.MaxValue;
            var body = new List<byte> { (byte)(v3 ? 3 : 2), 0, 0, 0 };
            body.AddRange(BigEndian(id, v3 ? 4 : 2));
            body.AddRange([0, 0]);
            body.AddRange("mime"u8.ToArray());
            body.Add(0);  // no name
            body.AddRange(Encoding.ASCII.GetBytes(XmpType));
            body.Add(0);
            return Boxed("infe", body);
        }

        private byte[] Reference(uint id, List<uint> to)
        {
            var body = new List<byte>(BigEndian(id, _referenceWidth));
            body.AddRange(BigEndian((ulong)to.Count, 2));
            foreach (var t in to) body.AddRange(BigEndian(t, _referenceWidth));
            return Boxed("cdsc", body);
        }

        /// <summary>The meta box, rebuilt: 'iloc' from <paramref name="locations"/> (plus the added item's place), and
        /// 'iinf' and 'iref' with the added item. Every other box is copied as it was, in its place.</summary>
        private byte[] Rebuilt(List<Location> locations, (uint Id, ulong Offset, ulong Length)? addedItem, (byte[] Infe, byte[] Reference)? added)
        {
            var b = _bytes;
            var body = new List<byte>();
            foreach (var box in _boxes)
            {
                switch (box.Type)
                {
                    case "iloc":
                        body.AddRange(Iloc(locations, addedItem));
                        break;
                    case "iinf" when added is { } a:
                    {
                        var version = b[box.Body];
                        var contents = b[box.Body..box.End].ToList();
                        var countWidth = version == 0 ? 2 : 4;
                        var count = (ulong)_items.Count + 1;
                        if (version == 0 && count > ushort.MaxValue) throw new MetadataRefusedException("It has too many parts to add an XMP.");
                        var countBytes = BigEndian(count, countWidth);
                        for (var i = 0; i < countWidth; i++) contents[4 + i] = countBytes[i];
                        contents.AddRange(a.Infe);
                        body.AddRange(Boxed("iinf", contents));
                        break;
                    }
                    case "iref" when added is { } a:
                        body.AddRange(Boxed("iref", [.. b[box.Body..box.End], .. a.Reference]));
                        break;
                    default:
                        body.AddRange(b[box.At..box.End]);
                        break;
                }
            }
            if (!_boxes.Any(x => x.Type == "iref") && added is { } add) body.AddRange(Boxed("iref", [0, 0, 0, 0, .. add.Reference]));
            var outer = Children(b, 0, b.Length)[0];
            return Boxed("meta", [.. b[outer.Body..(outer.Body + 4)], .. body]);
        }

        private byte[] Iloc(List<Location> locations, (uint Id, ulong Offset, ulong Length)? added)
        {
            var all = added is { } a ? [.. locations, new Location(a.Id, 0, 0, 0, [(0, a.Offset, a.Length)])] : locations;
            var iloc = Find("iloc");
            var body = new List<byte> { (byte)_ilocVersion, _bytes[iloc.Body + 1], _bytes[iloc.Body + 2], _bytes[iloc.Body + 3],
                (byte)((_offsetSize << 4) | _lengthSize), (byte)((_baseSize << 4) | _indexSize) };
            if (_ilocVersion < 2 && all.Count > ushort.MaxValue) throw new MetadataRefusedException("It has too many parts to add an XMP.");
            body.AddRange(BigEndian((ulong)all.Count, _ilocVersion < 2 ? 2 : 4));
            foreach (var l in all)
            {
                body.AddRange(BigEndian(l.Id, _ilocVersion < 2 ? 2 : 4));
                if (_ilocVersion is 1 or 2) body.AddRange(BigEndian((ulong)l.Method, 2));
                body.AddRange(BigEndian((ulong)l.Reference, 2));
                body.AddRange(BigEndian(l.Base, _baseSize));
                body.AddRange(BigEndian((ulong)l.Extents.Count, 2));
                foreach (var e in l.Extents)
                {
                    body.AddRange(BigEndian(e.Index, _indexSize));
                    body.AddRange(BigEndian(e.Offset, _offsetSize));
                    body.AddRange(BigEndian(e.Length, _lengthSize));
                }
            }
            return Boxed("iloc", body);
        }
    }

    private static byte[] Boxed(string type, List<byte> body)
    {
        var box = new byte[8 + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] BigEndian(ulong value, int size)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(value >> (8 * (size - 1 - i)));
        return b;
    }

    private static bool Valid(int size) => size is 0 or 4 or 8;

    private static bool Fits(long value, int size) => value >= 0 && size switch
    {
        0 => value == 0,
        2 => value <= ushort.MaxValue,
        4 => value <= uint.MaxValue,
        _ => true,
    };

    private static MetadataRefusedException Unfollowable() => new("It isn't a HEIC file that can be followed.");

    /// <summary>Reads big-endian values within a box's bounds, refusing anything past them.</summary>
    private ref struct Reader(ReadOnlySpan<byte> b, int at, int end)
    {
        private readonly ReadOnlySpan<byte> _b = b;
        public int At { get; private set; } = at;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || At + n > end) throw Unfollowable();
            var s = _b.Slice(At, n);
            At += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public byte U8() => Take(1)[0];
        public uint U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public ReadOnlySpan<byte> Bytes(int n) => Take(n);

        public ulong UInt(int size) => size switch
        {
            0 => 0,
            4 => U32(),
            8 => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            _ => throw Unfollowable(),
        };
    }
}
