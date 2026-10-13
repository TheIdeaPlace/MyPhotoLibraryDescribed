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

    /// <summary>A HEIF file of <paramref name="items"/>: ftyp, meta (hdlr, pitm 1, iinf, iref, iloc version 1 with 4-byte
    /// offsets and lengths), then one mdat holding every item's bytes in order.</summary>
    private static byte[] Heif(Item[] items, Func<byte[], byte[]>? afterMdat = null, int extentsForXmp = 1, int methodForXmp = 0)
    {
        var ftyp = Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1"u8.ToArray(), "heic"u8.ToArray());
        byte[] Meta(uint dataStart)
        {
            var hdlr = Box("hdlr", U32(0), U32(0), "pict"u8.ToArray(), new byte[12], [0]);
            var pitm = Box("pitm", U32(0), U16(1));
            var infes = items.Select(i => Box("infe", [2, 0, 0, 0], U16(i.Id), U16(0), Encoding.ASCII.GetBytes(i.Type), [0],
                i.Type == "mime" ? [.. Encoding.ASCII.GetBytes(i.ContentType), 0, .. Encoding.ASCII.GetBytes(i.Encoding), .. i.Encoding.Length > 0 ? new byte[] { 0 } : []] : [])).ToArray();
            var iinf = Box("iinf", [U32(0), U16((uint)items.Length), .. infes]);
            var refs = items.Where(i => i.Describes is not null).Select(i => Box("cdsc", [U16(i.Id), U16((uint)i.Describes!.Length), .. i.Describes.Select(U16)])).ToArray();
            var iref = Box("iref", [U32(0), .. refs]);
            var entries = new List<byte[]>();
            var at = dataStart;
            foreach (var i in items)
            {
                var isXmp = i.Type == "mime";
                var method = isXmp ? methodForXmp : 0;
                if (isXmp && extentsForXmp == 2)
                {
                    var half = (uint)i.Data.Length / 2;
                    entries.Add([.. U16(i.Id), .. U16((uint)method), .. U16(0), .. U16(2), .. U32(at), .. U32(half), .. U32(at + half), .. U32((uint)i.Data.Length - half)]);
                }
                else entries.Add([.. U16(i.Id), .. U16((uint)method), .. U16(0), .. U16(1), .. U32(at), .. U32((uint)i.Data.Length)]);
                at += (uint)i.Data.Length;
            }
            var iloc = Box("iloc", [[1, 0, 0, 0], [0x44, 0x00], U16((uint)items.Length), .. entries]);
            return Box("meta", U32(0), hdlr, pitm, iinf, iref, iloc);
        }
        var size = Meta(0).Length;
        var meta = Meta((uint)(ftyp.Length + size + 8));
        var mdat = Box("mdat", items.SelectMany(i => i.Data).ToArray());
        byte[] file = [.. ftyp, .. meta, .. mdat];
        return afterMdat is null ? file : afterMdat(file);
    }

    /// <summary>Each item's bytes, by reading the file's iloc (version 1, 4-byte offsets and lengths, as Heif writes it).</summary>
    private static Dictionary<uint, byte[]> ItemBytes(byte[] file)
    {
        var at = file.AsSpan().IndexOf("iloc"u8) - 4;
        var p = at + 8 + 4 + 2;
        var count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p));
        p += 2;
        var items = new Dictionary<uint, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p));
            var extents = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p + 6));
            p += 8;
            var bytes = new List<byte>();
            for (var e = 0; e < extents; e++)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 4));
                bytes.AddRange(file[offset..(offset + length)]);
                p += 8;
            }
            items[id] = [.. bytes];
        }
        return items;
    }

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
        Assert.Equal(3, MediaMetadata.VersionNeeded(@"C:\photos\IMG_1.HEIC"));
        Assert.Equal(2, MediaMetadata.VersionNeeded(@"C:\photos\IMG_1.JPG"));
    }
}
