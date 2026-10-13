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
///   so the file keeps its size. Otherwise it goes at the end: in place of the last 'mdat' box when that holds only this
///   XMP (as it does once this has moved it), or else in a new 'mdat' box with the old bytes blanked, so an old caption
///   doesn't linger; only the item's place and length in 'iloc' change.
/// - Without one, an item is added, hidden as Windows' is: an entry in 'iinf', a 'cdsc' reference to the photo (and to
///   anything the EXIF item describes alongside it, such as the gain map's tone map, as Windows does), and a place in
///   'iloc', with the XMP in a new 'mdat' box at the end. The meta box grows, so every item stored after it has its place
///   moved by that much.
/// - The EXIF item's title, description and rating are patched in place, as for JPEG and PNG: Windows' property handler
///   wrote the caption there too, and ImageIO reads EXIF's description ahead of the XMP.
/// - Before it's returned, the new file is read back: every item but these two must hold exactly the bytes it held.
/// - The pictures, the gain map and depth map stay byte for byte. Anything it can't follow (a burst or animation, an
///   item in another file, an XMP stored inside the meta box, compressed or in pieces, a box it can't read) is refused
///   with <see cref="MetadataRefusedException"/>, before anything is written.
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

    /// <summary>The caption and accessibility fields from the photo's XMP item, reading only the boxes up to the meta
    /// box, the meta box, and the item; nulls when it has none.</summary>
    public static (string? Caption, string? AltText, string? ExtendedDescription) Fields(Stream file)
    {
        var head = new byte[Math.Min(file.Length, 64)];
        file.Position = 0;
        file.ReadExactly(head);
        if (!LooksLikeHeif(head)) throw Unfollowable();
        var metaBox = TopBoxes(file, untilMeta: true).FirstOrDefault(x => x.Type == "meta");
        if (metaBox.Type is null) throw Unfollowable();
        if (metaBox.Size > 16 << 20) throw new MetadataRefusedException("Its metadata is too large to read.");
        var bytes = new byte[metaBox.Size];
        file.Position = metaBox.At;
        file.ReadExactly(bytes);
        var meta = Meta.Parse(bytes, metaBox.At, file.Length);
        if (meta.PhotoXmp() is not { } item) return (null, null, null);
        var (at, length) = meta.Place(item);
        if (length > 4 << 20) throw new MetadataRefusedException("Its metadata is too large to read.");
        var packet = new byte[length];
        file.Position = at;
        file.ReadExactly(packet);
        return XmpPacket.Fields(XmpPacket.Parse(packet));
    }

    /// <summary>The file with the changes made to the photo's XMP (and EXIF, in place). Throws
    /// <see cref="MetadataRefusedException"/> for a file it can't safely change.</summary>
    public static byte[] Apply(byte[] heif, MetadataChanges changes)
    {
        if (!LooksLikeHeif(heif)) throw Unfollowable();
        using var stream = new MemoryStream(heif, writable: false);
        var top = TopBoxes(stream, untilMeta: false);
        if (top.Any(x => x.Type == "moov")) throw new MetadataRefusedException("It's a burst or animation, which isn't changed.");
        var metaBox = top.FirstOrDefault(x => x.Type == "meta");
        if (metaBox.Type is null || metaBox.LargeSize) throw Unfollowable();
        var meta = Meta.Parse(heif.AsSpan((int)metaBox.At, (int)metaBox.Size), metaBox.At, heif.Length);
        meta.RefuseOtherFiles();

        // EXIF's own title, description and rating, in place: the same size, nothing moved.
        var working = (byte[])heif.Clone();
        var exif = meta.PhotoExif();
        if (exif is { } exifId)
        {
            if (meta.TryPlace(exifId) is { Length: >= 4 } exifPlace
                && exifPlace.At + 4 + BinaryPrimitives.ReadUInt32BigEndian(working.AsSpan((int)exifPlace.At)) is var tiff
                && tiff + 8 <= exifPlace.At + exifPlace.Length)
                XmpPacket.PatchExif(working, (int)tiff, (int)(exifPlace.At + exifPlace.Length), changes, clearDescription: changes.Caption is not null);
            else
                Log.Write("save", "A HEIC's EXIF is stored in a way that isn't patched; its own title or description is left as it is");
        }

        byte[] output;
        uint xmpId;
        if (meta.PhotoXmp() is { } item)
        {
            xmpId = item;
            var (at, length) = meta.Place(item);
            var document = XmpPacket.Parse(working.AsSpan((int)at, (int)length));
            XmpPacket.Edit(document, changes);
            var packet = XmpPacket.Serialize(document, (int)length);
            if (packet.Length == length)
            {
                packet.CopyTo(working, (int)at);
                output = working;
            }
            else if (top[^1] is { Type: "mdat", LargeSize: false } last && last.At + 8 == at && last.End == at + length && last.End == working.Length)
            {
                // Already alone at the end (moved there before): that box is rewritten, rather than another added.
                var moved = meta.Moved(item, at, packet.Length);
                output = new byte[last.At + 8 + packet.Length];
                working.AsSpan(0, (int)last.At).CopyTo(output);
                moved.CopyTo(output, (int)metaBox.At);
                WriteMdat(output, (int)last.At, packet);
            }
            else
            {
                if (top[^1].RunsToEnd) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
                // At the end, in a box of its own; only this item's offset and length change, in place.
                var moved = meta.Moved(item, working.Length + 8, packet.Length);
                output = new byte[working.Length + 8 + packet.Length];
                working.CopyTo(output, 0);
                moved.CopyTo(output, (int)metaBox.At);
                WriteMdat(output, working.Length, packet);
                // The old XMP, now unused, as XMP padding: spaces.
                output.AsSpan((int)at, (int)length).Fill((byte)' ');
            }
        }
        else
        {
            if (top[^1].RunsToEnd) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
            // No XMP for the photo yet: one is added, with room to grow.
            var fresh = XmpPacket.New();
            XmpPacket.Edit(fresh, changes);
            var xmp = XmpPacket.Serialize(fresh, 1);
            var (grown, id) = meta.WithXmpItem(working.Length, xmp.Length);
            xmpId = id;
            var delta = grown.Length - (int)metaBox.Size;
            output = new byte[working.Length + delta + 8 + xmp.Length];
            working.AsSpan(0, (int)metaBox.At).CopyTo(output);
            grown.CopyTo(output, (int)metaBox.At);
            working.AsSpan((int)metaBox.End).CopyTo(output.AsSpan((int)metaBox.At + grown.Length));
            WriteMdat(output, working.Length + delta, xmp);
        }

        AfterBuild?.Invoke(output);
        Check(heif, output, xmpId, exif);
        return output;
    }

    /// <summary>For tests: changes the new file before it's checked, to show a bad one is refused.</summary>
    internal static Action<byte[]>? AfterBuild { get; set; }

    /// <summary>The new file read back: the same items (and one more at most, the XMP), each but the XMP and EXIF
    /// holding exactly the bytes it held, and the XMP readable. Anything else and nothing is written.</summary>
    private static void Check(byte[] before, byte[] after, uint xmpId, uint? exifId)
    {
        // Whatever goes wrong here is this code's own mistake, not the file's, and is said so.
        static MetadataRefusedException Failed() => new("The change couldn't be checked, so it wasn't made.");
        try
        {
            var was = Items(before);
            var now = Items(after);
            if (!was.Keys.All(now.ContainsKey) || now.Count - was.Count is < 0 or > 1 || !now.ContainsKey(xmpId)) throw Failed();
            foreach (var (id, bytes) in was)
            {
                if (id == xmpId) continue;
                if (id == exifId ? bytes.Length != now[id].Length : !bytes.AsSpan().SequenceEqual(now[id])) throw Failed();
            }
            XmpPacket.Parse(now[xmpId]);
        }
        catch (Exception ex)
        {
            Log.Write("save", $"A HEIC change failed its own check ({ex.GetType().Name}); the file was left as it is");
            throw Failed();
        }
    }

    /// <summary>Every item's bytes, by its id: stored in the file (method 0) or in the meta box's 'idat' (method 1).
    /// Items made from other items (method 2) aren't listed.</summary>
    internal static Dictionary<uint, byte[]> Items(byte[] heif)
    {
        using var stream = new MemoryStream(heif, writable: false);
        var metaBox = TopBoxes(stream, untilMeta: false).First(x => x.Type == "meta");
        return Meta.Parse(heif.AsSpan((int)metaBox.At, (int)metaBox.Size), metaBox.At, heif.Length).ItemBytes(heif);
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

    /// <summary>The file's top-level boxes, which must cover it exactly; or, reading, only those up to the meta box.</summary>
    private static List<TopBox> TopBoxes(Stream file, bool untilMeta)
    {
        var list = new List<TopBox>();
        var header = new byte[16];
        long at = 0;
        while (at < file.Length)
        {
            if (file.Length - at < 8) throw Unfollowable();
            file.Position = at;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var large = false;
            var toEnd = false;
            if (size == 1)
            {
                if (file.Length - at < 16) throw Unfollowable();
                file.ReadExactly(header, 8, 8);
                var big = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (big > (ulong)(file.Length - at)) throw Unfollowable();
                size = (long)big;
                large = true;
            }
            else if (size == 0)
            {
                size = file.Length - at;
                toEnd = true;
            }
            if (size < 8 || size > file.Length - at) throw Unfollowable();
            var box = new TopBox(Encoding.ASCII.GetString(header, 4, 4), at, size, large, toEnd);
            list.Add(box);
            if (untilMeta && box.Type == "meta") return list;
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
            if (end - at < 8) throw Unfollowable();
            long size = BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
            // Boxes inside the meta box are small: a large or open-ended size there isn't followed.
            if (size < 8 || size > end - at) throw Unfollowable();
            list.Add(new Box(Encoding.ASCII.GetString(b.Slice(at + 4, 4)), at, at + 8, (int)(at + size)));
            at += (int)size;
        }
        return list;
    }

    /// <summary>An 'iloc' entry: where an item's bytes are.</summary>
    private sealed record Location(uint Id, int Method, int Reference, ulong Base, List<(ulong Index, ulong Offset, ulong Length)> Extents);

    /// <summary>The meta box, read: the boxes it holds, the photo's item, the item types and descriptions ('iinf',
    /// 'iref'), the groups ('grpl', whose ids share the items' numbering) and places ('iloc').</summary>
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
        private readonly List<uint> _groups = [];
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
            meta.ReadGroups(bytes);
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

        private void ReadGroups(ReadOnlySpan<byte> b)
        {
            foreach (var grpl in _boxes.Where(x => x.Type == "grpl"))
                foreach (var group in Children(b, grpl.Body, grpl.End))
                {
                    // An EntityToGroupBox: a full box, then its group_id.
                    var p = new Reader(b, group.Body, group.End);
                    p.Skip(4);
                    _groups.Add(p.U32());
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
                if (baseOffset > long.MaxValue / 4 || extents.Any(x => x.Item2 > long.MaxValue / 4 || x.Item3 > long.MaxValue / 4)) throw Unfollowable();
                _locations.Add(new Location(id, method, reference, baseOffset, extents));
            }
            if (p.At != iloc.End) throw Unfollowable();
        }

        /// <summary>Items whose bytes are in another file (a data reference other than this one) aren't moved, so a
        /// file with any isn't changed.</summary>
        public void RefuseOtherFiles()
        {
            if (_locations.Any(l => l.Reference != 0)) throw new MetadataRefusedException("Part of it is stored in a way that isn't changed.");
        }

        /// <summary>The XMP item describing the photo, if there is one.</summary>
        public uint? PhotoXmp() => Describing(IsXmp, "It has more than one XMP for the photo, which isn't changed.");

        /// <summary>The EXIF item describing the photo, if there is one.</summary>
        public uint? PhotoExif() => Describing(id => _items.TryGetValue(id, out var i) && i.Type == "Exif", "It has more than one EXIF for the photo, which isn't changed.");

        private uint? Describing(Func<uint, bool> kind, string twice)
        {
            var found = _references.Where(r => r.Type == "cdsc" && r.To.Contains(_primary) && kind(r.From)).Select(r => r.From).Distinct().ToList();
            return found.Count switch
            {
                0 => null,
                1 => found[0],
                _ => throw new MetadataRefusedException(twice),
            };
        }

        private bool IsXmp(uint id) =>
            _items.TryGetValue(id, out var item) && item.Type == "mime" && item.ContentType.Equals(XmpType, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where an XMP item's bytes are in the file: one extent, in this file, outside the meta box.</summary>
        public (long At, long Length) Place(uint id)
        {
            if (_items[id].Encoding.Length > 0) throw new MetadataRefusedException("Its XMP is compressed, which isn't changed.");
            return TryPlace(id) ?? throw new MetadataRefusedException("Its XMP is stored in a way that isn't changed.");
        }

        /// <summary>Where an item's bytes are, when they're one extent stored in this file, outside the meta box, and
        /// shared with no other item; null when they're stored another way.</summary>
        public (long At, long Length)? TryPlace(uint id)
        {
            var location = _locations.Where(l => l.Id == id).ToList();
            if (location.Count != 1) throw Unfollowable();
            var l = location[0];
            if (l.Method != 0 || l.Reference != 0 || l.Extents.Count != 1 || _offsetSize == 0 || _lengthSize == 0) return null;
            var at = (long)(l.Base + l.Extents[0].Offset);
            var length = (long)l.Extents[0].Length;
            if (length <= 0 || at + length > _fileLength || (at < _at + _bytes.Length && _at < at + length)) throw Unfollowable();
            foreach (var other in _locations.Where(o => o.Id != id && o.Method == 0))
                foreach (var e in other.Extents)
                {
                    var from = (long)(other.Base + e.Offset);
                    if (from < at + length && at < from + (long)e.Length) throw Unfollowable();
                }
            return (at, length);
        }

        /// <summary>Each item's bytes, for checking a change (see <see cref="Items"/>).</summary>
        public Dictionary<uint, byte[]> ItemBytes(byte[] file)
        {
            var idat = _boxes.FirstOrDefault(x => x.Type == "idat");
            var items = new Dictionary<uint, byte[]>();
            foreach (var l in _locations)
            {
                if (l.Method == 2) continue;
                var bytes = new List<byte>();
                foreach (var e in l.Extents)
                {
                    var start = (long)(l.Base + e.Offset);
                    var length = (long)e.Length;
                    if (l.Method == 0)
                    {
                        if (length == 0) length = file.Length - start;
                        if (start < 0 || length < 0 || start + length > file.Length) throw Unfollowable();
                        bytes.AddRange(file.AsSpan((int)start, (int)length));
                    }
                    else
                    {
                        if (idat.Type is null) throw Unfollowable();
                        var size = idat.End - idat.Body;
                        if (length == 0) length = size - start;
                        if (start < 0 || length < 0 || start + length > size) throw Unfollowable();
                        bytes.AddRange(_bytes.AsSpan(idat.Body + (int)start, (int)length));
                    }
                }
                if (!items.TryAdd(l.Id, [.. bytes])) throw Unfollowable();
            }
            return items;
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

        /// <summary>The meta box's bytes with an XMP item added for the photo, and its id. Its bytes go at the end of a
        /// file now <paramref name="fileLength"/> long, in a new 'mdat' box; every item after the meta box is moved by
        /// the meta box's growth.</summary>
        public (byte[] Bytes, uint Id) WithXmpItem(long fileLength, int xmpLength)
        {
            if (_offsetSize == 0 || _lengthSize == 0) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
            // Group ids share the items' numbering.
            var id = Math.Max(_items.Keys.Max(), _groups.DefaultIfEmpty(0u).Max()) + 1;
            if (id > ushort.MaxValue && (_ilocVersion < 2 || _referenceWidth == 2)) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
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
                if (l.Method != 0) { moved.Add(l); continue; }
                // A base offset past the meta box moves; otherwise each extent past it does.
                var baseMoves = _baseSize > 0 && (long)l.Base >= end;
                var baseOffset = baseMoves ? l.Base + delta : l.Base;
                if (!Fits((long)baseOffset, _baseSize)) throw new MetadataRefusedException("It's too large to add an XMP to.");
                var extents = new List<(ulong, ulong, ulong)>();
                foreach (var e in l.Extents)
                {
                    var abs = (long)(l.Base + e.Offset);
                    // Something stored inside the meta box itself would move in a way this doesn't follow.
                    if (abs >= _at && abs < end) throw Unfollowable();
                    var offset = !baseMoves && abs >= end ? e.Offset + delta : e.Offset;
                    if (!Fits((long)offset, _offsetSize)) throw new MetadataRefusedException("It's too large to add an XMP to.");
                    extents.Add((e.Index, offset, e.Length));
                }
                moved.Add(l with { Base = baseOffset, Extents = extents });
            }
            var xmpAt = fileLength + (long)delta + 8;
            if (!Fits(xmpAt, _offsetSize) || !Fits(xmpLength, _lengthSize)) throw new MetadataRefusedException("It's too large to add an XMP to.");
            var bytes = Rebuilt(moved, (id, (ulong)xmpAt, (ulong)xmpLength), (infe, reference));
            if (bytes.Length != draft.Length) throw Unfollowable();
            return (bytes, id);
        }

        /// <summary>The new item's entry: version 2 (or 3 for a large id), hidden (flags 1) as Windows' and Apple's
        /// metadata items are, type 'mime', no name, application/rdf+xml.</summary>
        private static List<byte> InfeBody(uint id)
        {
            var v3 = id > ushort.MaxValue;
            var body = new List<byte> { (byte)(v3 ? 3 : 2), 0, 0, 1 };
            body.AddRange(BigEndian(id, v3 ? 4 : 2));
            body.AddRange([0, 0]);
            body.AddRange("mime"u8.ToArray());
            body.Add(0);
            body.AddRange(Encoding.ASCII.GetBytes(XmpType));
            body.Add(0);
            return body;
        }

        private static byte[] Infe(uint id) => Boxed("infe", InfeBody(id));

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
                        if (version == 0 && count > ushort.MaxValue) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
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
            if (_ilocVersion < 2 && all.Count > ushort.MaxValue) throw new MetadataRefusedException("Its layout leaves no room to add to it safely.");
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

    /// <summary>Said as "IMG_1234.HEIC: … It was left as it is."</summary>
    private static MetadataRefusedException Unfollowable() => new("Its layout isn't one that can be changed safely.");

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
