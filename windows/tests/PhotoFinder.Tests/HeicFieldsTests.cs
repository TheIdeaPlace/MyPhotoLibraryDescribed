using System.Buffers.Binary;
using System.Text;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Tests;

/// <summary>Captions and accessibility fields in HEIC files (#31), written box by box (<see cref="HeifMetadata"/>), with
/// files laid out as an iPhone writes them: the photo, its EXIF, an HDR gain map with an XMP of its own, and, once Windows
/// has saved a Title, an XMP item for the photo.</summary>
[Collection(SharedState.Name)]
public sealed class HeicFieldsTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private const string GainMapXmp = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:HDRGainMap="http://ns.apple.com/HDRGainMap/1.0/" HDRGainMap:HDRGainMapVersion="131072"/></rdf:RDF></x:xmpmeta>""";

    private const string PhotoXmp = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title><rdf:Alt><rdf:li xml:lang="x-default">Windows title</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>""";

    /// <summary>An item: its id, type, content type (for 'mime'), bytes, and the items it describes ('cdsc').</summary>
    private sealed record Item(uint Id, string Type, byte[] Data, string ContentType = "", uint[]? Describes = null, string Encoding = "");

    private static Item[] IPhoneItems(bool photoXmp = false) =>
    [
        new(1, "hvc1", "THE PHOTO'S PICTURE DATA"u8.ToArray()),
        new(2, "Exif", "EXIF DATA, CAMERA AND DATE"u8.ToArray(), Describes: [1, 4]),
        new(3, "mime", Encoding.UTF8.GetBytes(GainMapXmp), "application/rdf+xml", [5]),
        new(4, "tmap", "TONE MAP"u8.ToArray()),
        new(5, "hvc1", "THE GAIN MAP'S PICTURE"u8.ToArray()),
        .. photoXmp ? new[] { new Item(6, "mime", Encoding.UTF8.GetBytes(PhotoXmp), "application/rdf+xml", [1, 4]) } : [],
    ];

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] U16(uint v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>How the meta box is written: iloc's version and field sizes (a base offset, when there is one, is the
    /// start of the mdat's contents, with each extent counted from it), an 'idat' item (7, its bytes in the meta box), a
    /// group whose id comes after the items', no 'iref', and the data reference of item 1.</summary>
    private sealed record Layout(int IlocVersion = 1, int OffsetSize = 4, int BaseSize = 0, bool Idat = false, uint? Group = null, bool NoIref = false, int Reference = 0);

    private static byte[] UInt(ulong v, int size) => size switch
    {
        0 => [],
        2 => U16((uint)v),
        4 => U32((uint)v),
        _ => [.. U32((uint)(v >> 32)), .. U32((uint)v)],
    };

    /// <summary>A HEIF file of <paramref name="items"/>: ftyp, meta (hdlr, pitm 1, iinf, iref, grpl, idat, iloc), then
    /// one mdat holding every item's bytes in order. iloc is version 1 with 4-byte offsets and lengths unless
    /// <paramref name="layout"/> says otherwise.</summary>
    private static byte[] Heif(Item[] items, Func<byte[], byte[]>? afterMdat = null, int extentsForXmp = 1, int methodForXmp = 0, Layout? layout = null)
    {
        var l = layout ?? new Layout();
        var ftyp = Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1"u8.ToArray(), "heic"u8.ToArray());
        var idatBytes = "PARAMETERS IN IDAT"u8.ToArray();
        var all = l.Idat ? [.. items, new Item(7, "hvcC", idatBytes)] : items;
        byte[] Meta(uint dataStart)
        {
            var hdlr = Box("hdlr", U32(0), U32(0), "pict"u8.ToArray(), new byte[12], [0]);
            var pitm = Box("pitm", U32(0), U16(1));
            var infes = all.Select(i => Box("infe", [2, 0, 0, 0], U16(i.Id), U16(0), Encoding.ASCII.GetBytes(i.Type), [0],
                i.Type == "mime" ? [.. Encoding.ASCII.GetBytes(i.ContentType), 0, .. Encoding.ASCII.GetBytes(i.Encoding), .. i.Encoding.Length > 0 ? new byte[] { 0 } : []] : [])).ToArray();
            var iinf = Box("iinf", [U32(0), U16((uint)all.Length), .. infes]);
            var refs = items.Where(i => i.Describes is not null).Select(i => Box("cdsc", [U16(i.Id), U16((uint)i.Describes!.Length), .. i.Describes.Select(U16)])).ToArray();
            var iref = Box("iref", [U32(0), .. refs]);
            byte[] grpl = l.Group is { } g ? Box("grpl", Box("altr", U32(0), U32(g), U32(2), U32(1), U32(5))) : [];
            byte[] idat = l.Idat ? Box("idat", idatBytes) : [];
            var idWidth = l.IlocVersion < 2 ? 2 : 4;
            var entries = new List<byte[]>();
            var at = dataStart;
            foreach (var i in all)
            {
                var inIdat = i.Id == 7 && l.Idat;
                var method = inIdat ? 1 : i.Type == "mime" ? methodForXmp : 0;
                var reference = i.Id == 1 ? l.Reference : 0;
                var head = new List<byte>(UInt(i.Id, idWidth));
                if (l.IlocVersion > 0) head.AddRange(U16((uint)method));
                head.AddRange(U16((uint)reference));
                var baseOffset = l.BaseSize > 0 && !inIdat ? dataStart : 0;
                head.AddRange(UInt(baseOffset, l.BaseSize));
                (ulong Offset, ulong Length)[] extents = inIdat ? [(0, (ulong)idatBytes.Length)]
                    : i.Type == "mime" && extentsForXmp == 2 ? [(at - baseOffset, (ulong)i.Data.Length / 2), (at - baseOffset + (ulong)i.Data.Length / 2, (ulong)i.Data.Length - (ulong)i.Data.Length / 2)]
                    : [(at - baseOffset, (ulong)i.Data.Length)];
                head.AddRange(U16((uint)extents.Length));
                foreach (var e in extents) head.AddRange([.. UInt(e.Offset, l.OffsetSize), .. UInt(e.Length, 4)]);
                entries.Add([.. head]);
                if (!inIdat) at += (uint)i.Data.Length;
            }
            var iloc = Box("iloc", [[(byte)l.IlocVersion, 0, 0, 0], [(byte)((l.OffsetSize << 4) | 4), (byte)(l.BaseSize << 4)], UInt((ulong)all.Length, idWidth), .. entries]);
            return l.NoIref ? Box("meta", U32(0), hdlr, pitm, iinf, grpl, idat, iloc) : Box("meta", U32(0), hdlr, pitm, iinf, iref, grpl, idat, iloc);
        }
        var size = Meta(0).Length;
        var meta = Meta((uint)(ftyp.Length + size + 8));
        var mdat = Box("mdat", items.SelectMany(i => i.Data).ToArray());
        byte[] file = [.. ftyp, .. meta, .. mdat];
        return afterMdat is null ? file : afterMdat(file);
    }

    /// <summary>Each item's bytes, as the file's own iloc places them.</summary>
    private static Dictionary<uint, byte[]> ItemBytes(byte[] file) => HeifMetadata.Items(file);

    private static (string? Caption, string? AltText, string? ExtendedDescription) Read(byte[] file)
    {
        using var s = new MemoryStream(file);
        return HeifMetadata.Fields(s);
    }

    [Fact]
    public void AnIPhonesHeic_GetsAnXmpForThePhoto_LeavingTheGainMapsXmpAndEveryItemAsTheyWere()
    {
        var before = Heif(IPhoneItems());
        // The XMP it has describes the gain map, not the photo: no caption.
        Assert.Equal((null, null, null), Read(before));

        var after = HeifMetadata.Apply(before, new MetadataChanges { Caption = "A dog on a beach.", AltText = "A dog.", Title = "A dog on a beach.", Stars = 4 });
        Assert.Equal(("A dog on a beach.", "A dog.", null), Read(after));
        var was = ItemBytes(before);
        var now = ItemBytes(after);
        Assert.Equal(was.Count + 1, now.Count);
        // Every item's bytes are where its (moved) place says, unchanged.
        foreach (var (id, bytes) in was) Assert.Equal(bytes, now[id]);
        var xmp = Encoding.UTF8.GetString(now[6]);
        Assert.Contains("dc:title", xmp);
        Assert.Contains("<xmp:Rating>4</xmp:Rating>", xmp.Replace(" ", ""));
        // It describes what the EXIF describes: the photo and its tone map.
        Assert.Contains("dc:description", xmp);
        Assert.True(after.AsSpan().IndexOf(Box("cdsc", U16(6), U16(2), U16(1), U16(4))) > 0);
        // The pictures' bytes come after the grown meta box, in the same order.
        Assert.True(after.AsSpan().IndexOf("THE PHOTO'S PICTURE DATA"u8) > before.AsSpan().IndexOf("THE PHOTO'S PICTURE DATA"u8));

        // The next change fits in the new XMP's padding: the file keeps its size.
        var again = HeifMetadata.Apply(after, new MetadataChanges { Caption = "A brown dog.", AltText = "A brown dog." });
        Assert.Equal(after.Length, again.Length);
        Assert.Equal(("A brown dog.", "A brown dog.", null), Read(again));
        Assert.Equal(ItemBytes(after).Where(i => i.Key != 6), ItemBytes(again).Where(i => i.Key != 6));
    }

    [Fact]
    public void AHeicWindowsTitled_HasThatXmpChanged_MovedToTheEndWhenItGrows_WithTheOldOneBlanked()
    {
        var before = Heif(IPhoneItems(photoXmp: true));
        Assert.Equal((null, null, null), Read(before));
        var long_ = string.Concat(Enumerable.Repeat("A dog runs along the beach. ", 40)).Trim();
        var after = HeifMetadata.Apply(before, new MetadataChanges { Caption = long_, AltText = "A dog." });
        Assert.Equal((long_, "A dog.", null), Read(after));
        var was = ItemBytes(before);
        var now = ItemBytes(after);
        Assert.Equal(was.Keys, now.Keys);
        foreach (var id in was.Keys.Where(k => k != 6)) Assert.Equal(was[id], now[id]);
        Assert.Contains("Windows title", Encoding.UTF8.GetString(now[6]));
        // The old copy is gone from the file; only the meta box's two fields and the old bytes changed before the end.
        Assert.Equal(1, CountOf(after, "Windows title"));
        Assert.Equal(before[..before.AsSpan().IndexOf("iloc"u8)], after[..before.AsSpan().IndexOf("iloc"u8)]);
        Assert.True(after.Length > before.Length);
    }

    private static int CountOf(byte[] b, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        var count = 0;
        for (var at = b.AsSpan().IndexOf(needle); at >= 0; at = b.AsSpan(at + 1).IndexOf(needle) is var next and >= 0 ? at + 1 + next : -1) count++;
        return count;
    }

    public static TheoryData<string> Refused() =>
    [
        "compressed", "two extents", "inside the meta box", "a sequence", "runs to the end", "two photo xmps", "truncated", "not heif",
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void WhatItCantFollow_IsRefused(string what)
    {
        var file = what switch
        {
            "compressed" => Heif([.. IPhoneItems().Take(2), new Item(6, "mime", "deflated"u8.ToArray(), "application/rdf+xml", [1], "deflate")]),
            "two extents" => Heif(IPhoneItems(photoXmp: true), extentsForXmp: 2),
            "inside the meta box" => Heif(IPhoneItems(photoXmp: true), methodForXmp: 1),
            "a sequence" => Heif(IPhoneItems(), f => [.. f, .. Box("moov", new byte[8])]),
            "runs to the end" => Heif(IPhoneItems(), f => [.. f, 0, 0, 0, 0, .. "free"u8.ToArray(), 1, 2, 3]),
            "two photo xmps" => Heif([.. IPhoneItems(photoXmp: true), new Item(7, "mime", Encoding.UTF8.GetBytes(PhotoXmp), "application/rdf+xml", [1])]),
            "truncated" => Heif(IPhoneItems())[..200],
            _ => "not a heif file at all, just some bytes"u8.ToArray(),
        };
        Assert.ThrowsAny<MetadataRefusedException>(() => HeifMetadata.Apply(file, new MetadataChanges { Caption = "A dog." }));
    }

    [Fact]
    public async Task ThroughTheApp_AHeicGetsItsCaptionAndAccessibilityFields_AndKeepsItsDate()
    {
        var path = _lib.PathOf("IMG_0001.HEIC");
        File.WriteAllBytes(path, Heif(IPhoneItems()));
        var date = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, date);
        Assert.True(XmpFields.CanWrite(path) && XmpFields.EditsInPlace(path));
        var item = new MediaItem(path, new FileInfo(path).Length, date, date);

        var result = await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(item, "A dog on a beach.")]);
        Assert.Equal(1, result.Succeeded);
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(new XmpFields.Fields("A dog on a beach.", "A dog on a beach.", "", HasCaption: true), fields);
        Assert.Equal(date, File.GetLastWriteTimeUtc(path));
        Assert.Equal("THE GAIN MAP'S PICTURE"u8.ToArray(), ItemBytes(File.ReadAllBytes(path))[5]);

        // A HEIC that isn't one is said to be the wrong kind, and left alone.
        var fake = _lib.PathOf("fake.heic");
        File.WriteAllBytes(fake, "\xFF\xD8\xFF not a heic"u8.ToArray());
        var refused = await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(new MediaItem(fake, 14, date, date), "A dog.")]);
        Assert.Equal(0, refused.Succeeded);
    }

    [Fact]
    public void OnlyHeicDetails_AreReadAgain_NotTheWholeLibrarys()
    {
        Assert.Equal(3, MetadataCache.VersionNeeded(@"C:\photos\IMG_1.HEIC"));
        Assert.Equal(2, MetadataCache.VersionNeeded(@"C:\photos\IMG_1.JPG"));
    }
    public static TheoryData<int, int, int, bool, bool> Layouts() => new()
    {
        { 0, 4, 0, false, false },
        { 1, 8, 0, true, false },
        { 1, 4, 4, true, false },
        { 2, 4, 0, false, true },
        { 2, 8, 4, true, true },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void AnXmpIsAdded_InEveryWayIlocCanBeWritten(int version, int offsetSize, int baseSize, bool idat, bool noIref)
    {
        var layout = new Layout(version, offsetSize, baseSize, idat, NoIref: noIref);
        var before = Heif(IPhoneItems(), layout: layout);
        var after = HeifMetadata.Apply(before, new MetadataChanges { Caption = "A dog.", AltText = "A dog." });
        if (noIref)
        {
            // With no iref, nothing describes the photo yet: the new XMP describes it alone.
            Assert.True(after.AsSpan().IndexOf(Box("cdsc", U16(idat ? 8u : 6u), U16(1), U16(1))) > 0);
        }
        Assert.Equal(("A dog.", "A dog.", null), Read(after));
        var was = ItemBytes(before);
        var now = ItemBytes(after);
        foreach (var (id, bytes) in was) Assert.Equal(bytes, now[id]);
        Assert.Equal(was.Count + 1, now.Count);
    }

    [Fact]
    public void TheNewItemsId_ComesAfterGroupsToo_AndItsHidden()
    {
        // HEIF numbers groups and items together: a group 9 after items 1 to 5 means the XMP is 10.
        var after = HeifMetadata.Apply(Heif(IPhoneItems(), layout: new Layout(Group: 9)), new MetadataChanges { Caption = "A dog." });
        Assert.True(ItemBytes(after).ContainsKey(10));
        // infe version 2, flags 1 (hidden), id 10.
        Assert.True(after.AsSpan().IndexOf((byte[])[.. "infe"u8.ToArray(), 2, 0, 0, 1, 0, 10]) > 0);
    }

    [Fact]
    public void ItemsInAnotherFile_AreRefused()
    {
        var file = Heif(IPhoneItems(), layout: new Layout(Reference: 1));
        Assert.Throws<MetadataRefusedException>(() => HeifMetadata.Apply(file, new MetadataChanges { Caption = "A dog." }));
    }

    /// <summary>A TIFF block as EXIF holds it, with ImageDescription (ASCII) and XPTitle (UTF-16) both
    /// <paramref name="text"/>, as Windows' property handler writes a HEIC's Title, after the HEIF Exif item's 4-byte
    /// offset to it.</summary>
    private static byte[] ExifWith(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var utf16 = Encoding.Unicode.GetBytes(text + "\0");
        var tiff = new List<byte> { 0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 2, 0 };
        var data = 8 + 2 + 2 * 12 + 4;
        void Entry(ushort tag, ushort type, int count, int offset) =>
            tiff.AddRange([(byte)tag, (byte)(tag >> 8), (byte)type, (byte)(type >> 8), (byte)count, (byte)(count >> 8), 0, 0, (byte)offset, (byte)(offset >> 8), 0, 0]);
        Entry(0x010E, 2, ascii.Length, data);
        Entry(0x9C9B, 1, utf16.Length, data + ascii.Length);
        tiff.AddRange([0, 0, 0, 0]);
        tiff.AddRange(ascii);
        tiff.AddRange(utf16);
        return [0, 0, 0, 0, .. tiff];
    }

    [Fact]
    public void AnOldCaptionInExif_IsEmptiedInPlace_SoTheMacDoesntReadItAheadOfTheNewOne()
    {
        var items = IPhoneItems(photoXmp: true);
        items[1] = items[1] with { Data = ExifWith("Windows title") };
        var before = Heif(items);
        var after = HeifMetadata.Apply(before, new MetadataChanges { Caption = "A dog.", Title = "A dog.", ClearOwnTitle = "Windows title" });
        var exif = ItemBytes(after)[2];
        Assert.Equal(ItemBytes(before)[2].Length, exif.Length);
        Assert.Equal(-1, exif.AsSpan().IndexOf("Windows title"u8));
        Assert.Equal(-1, exif.AsSpan().IndexOf(Encoding.Unicode.GetBytes("Windows title")));
        Assert.Equal(("A dog.", null, null), Read(after));
    }

    [Fact]
    public void AnXmpAloneAtTheEnd_IsRewrittenThere_RatherThanAnotherBoxAdded()
    {
        var first = HeifMetadata.Apply(Heif(IPhoneItems()), new MetadataChanges { Caption = "A dog." });
        var boxes = CountOf(first, "mdat");
        var long_ = string.Concat(Enumerable.Repeat("A dog runs along the beach at sunset. ", 120)).Trim();
        var grown = HeifMetadata.Apply(first, new MetadataChanges { Caption = long_ });
        Assert.Equal(boxes, CountOf(grown, "mdat"));
        Assert.Equal((long_, null, null), Read(grown));
        Assert.Equal(ItemBytes(first).Where(i => i.Key != 6), ItemBytes(grown).Where(i => i.Key != 6));
    }

    [Fact]
    public void ReadingStopsAtTheMetaBox_SoTrailingBytesItDoesntNeedDontMatter()
    {
        var after = HeifMetadata.Apply(Heif(IPhoneItems()), new MetadataChanges { Caption = "A dog." });
        byte[] trailing = [.. after, 1, 2, 3];
        Assert.Equal(("A dog.", null, null), Read(trailing));
    }
}
