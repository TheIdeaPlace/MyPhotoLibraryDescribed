using PhotoFinder.Services;

namespace PhotoFinder.Tests;

/// <summary>Captions and accessibility fields in files (#31, #62), matching the Mac's FileMetadataWriter.</summary>
[Collection(SharedState.Name)]
public sealed class FileFieldsTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void AltText_IsTheDescription_OrTheSentencesThatFitIn250()
    {
        Assert.Equal("A dog on a beach.", FileFields.AltText("  A dog on a beach.  "));
        Assert.Null(FileFields.ExtendedDescription("A dog on a beach."));

        var first = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. ";
        var second = "Waves break behind it and two people walk in the distance near the dunes. ";
        var third = "The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        var long3 = first + second + third;
        Assert.True(long3.Length > 250);
        var alt = FileFields.AltText(long3);
        Assert.Equal((first + second).Trim(), alt);
        Assert.Equal(long3.Trim(), FileFields.ExtendedDescription(long3));

        // One sentence too long: whole words, then "…", with no comma left before it.
        var words = string.Join(", ", Enumerable.Repeat("a dog on a beach", 30));
        var cut = FileFields.AltText(words);
        Assert.True(cut.Length <= 250);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain(",…", cut);

        // Never half an emoji, and any whitespace is a word break.
        var emoji = string.Concat(Enumerable.Repeat("🐕", 200));
        var cutEmoji = FileFields.AltText(emoji);
        Assert.False(char.IsHighSurrogate(cutEmoji[^2]), "A character was split.");
        var tabs = string.Join("\t", Enumerable.Repeat("dog", 100));
        Assert.EndsWith("dog…", FileFields.AltText(tabs));

        // Full-width sentence ends, with no space after them.
        var cjk = string.Concat(Enumerable.Repeat("海辺を走る犬がいます。", 30));
        var cjkAlt = FileFields.AltText(cjk);
        Assert.EndsWith("。", cjkAlt);
        Assert.True(cjkAlt.Length <= 250);
    }

    [Fact]
    public void Replaced_ListsWhatsAlreadyThere_ButNotEmptyOrMatchingFields()
    {
        Assert.Empty(FileFields.Replaced("", "", "", "A dog."));
        Assert.Empty(FileFields.Replaced("A dog.", "A dog.", "", "A dog."));
        Assert.Equal([("caption", "Mine")], FileFields.Replaced("Mine", "", "", "A dog."));
        Assert.Equal([("caption", "Mine"), ("alt text", "Other app")], FileFields.Replaced("Mine", "Other app", "", "A dog."));
        // Another app's extended description would be cleared (or replaced): that's asked about too.
        Assert.Equal([("extended description", "A long one from IDT.")], FileFields.Replaced("", "", "A long one from IDT.", "A dog."));
    }

    [Fact]
    public void ExplorersTitle_FollowsTheCaption_OnlyWhenEmptyOrTheOldCaption()
    {
        Assert.True(FileFields.TitleKeepsInStep("", "Old"));
        Assert.True(FileFields.TitleKeepsInStep("Old", "Old"));
        Assert.False(FileFields.TitleKeepsInStep("A title someone wrote", "Old"));
    }

    private static byte[] Scan(byte[] b)
    {
        for (var i = 2; i < b.Length - 1; i++) if (b[i] == 0xFF && b[i + 1] == 0xDA) return b[i..];
        return [];
    }

    [Fact]
    public async Task AJpegWithRoom_GetsTheFieldsInPlace_KeepingItsPictureDateTitleAndRating()
    {
        var path = _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { Title = "My title", Rating = 4, DateTaken = new DateTime(2022, 6, 1, 10, 0, 0) });
        var before = await MetadataReader.ReadAsync(path);
        var scan = Scan(File.ReadAllBytes(path));
        var modified = File.GetLastWriteTimeUtc(path);

        await XmpFields.WriteAsync(path, "A dog on a beach.", "A dog.", "");
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(new XmpFields.Fields("A dog on a beach.", "A dog.", "", HasCaption: true), fields);
        Assert.True(scan.AsSpan().SequenceEqual(Scan(File.ReadAllBytes(path))), "The picture changed.");
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var after = await MetadataReader.ReadAsync(path);
        Assert.Equal(before.DateTaken, after.DateTaken);
        Assert.Equal(before.Rating, after.Rating);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
        // The standard caption is read first, ahead of the Title.
        Assert.Equal("A dog on a beach.", after.Caption);
        Assert.Equal("A dog.", after.AltText);

        // Cleared: it stays cleared, rather than the Title someone wrote taking its place.
        await XmpFields.WriteAsync(path, "", null, null);
        Assert.Equal("", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("", (await MetadataReader.ReadAsync(path)).Caption);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
    }

    /// <summary>The JPEG without its APP segments (EXIF, XMP and their padding), so there's no room for the fields.</summary>
    private static byte[] WithoutRoom(byte[] b)
    {
        var output = new List<byte> { 0xFF, 0xD8 };
        var i = 2;
        while (b[i] == 0xFF && b[i + 1] is >= 0xE1 and <= 0xEF)
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        output.AddRange(b[i..]);
        return output.ToArray();
    }

    private static async Task<byte[]> PixelsAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync();
        return pixels.DetachPixelData();
    }

    [Fact]
    public async Task AJpegWithoutRoom_IsRewrittenBesideItAndSwappedIn_WithTheSamePixelsAndDate()
    {
        var path = _lib.AddJpeg("tight.jpg");
        File.WriteAllBytes(path, WithoutRoom(File.ReadAllBytes(path)));
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        var size = new FileInfo(path).Length;
        var pixels = await PixelsAsync(path);

        await XmpFields.WriteAsync(path, "A dog.", "A dog.", "");
        Assert.True(new FileInfo(path).Length > size, "It wasn't rewritten with room.");
        Assert.Equal("A dog.", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal(pixels, await PixelsAsync(path));
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
        // Nothing left beside it.
        Assert.Equal(["tight.jpg"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ATiff_GetsTheFields_KeepingItsPixels()
    {
        var path = _lib.PathOf("a.tif");
        TestLibrary.RunSta(() =>
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 6, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null,
                Enumerable.Range(0, 8 * 6 * 3).Select(i => (byte)i).ToArray(), 8 * 3);
            var encoder = new System.Windows.Media.Imaging.TiffBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        var pixels = await PixelsAsync(path);
        await XmpFields.WriteAsync(path, "A gradient.", "A gradient.", "");
        Assert.Equal("A gradient.", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal(pixels, await PixelsAsync(path));
    }

    // MARK: JPEGs changed segment by segment (#86)

    private static readonly byte[] XmpSignature = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    /// <summary>The header segments (marker, offset, length including the marker), up to the picture's start.</summary>
    private static List<(byte Marker, int Offset, int Length)> Segments(byte[] b)
    {
        var list = new List<(byte, int, int)>();
        var i = 2;
        while (b[i] == 0xFF && b[i + 1] is not (0xDA or 0xD9))
        {
            var length = 2 + ((b[i + 2] << 8) | b[i + 3]);
            list.Add((b[i + 1], i, length));
            i += length;
        }
        return list;
    }

    private static bool IsXmp(byte[] b, (byte Marker, int Offset, int Length) s) =>
        s.Marker == 0xE1 && b.AsSpan(s.Offset + 4).StartsWith(XmpSignature);

    /// <summary>The file with its XMP segment taken out: everything My Photo Library Described must leave as it was.</summary>
    private static byte[] WithoutXmp(byte[] b)
    {
        var output = new List<byte>();
        var at = 0;
        foreach (var s in Segments(b).Where(s => IsXmp(b, s)))
        {
            output.AddRange(b[at..s.Offset]);
            at = s.Offset + s.Length;
        }
        output.AddRange(b[at..]);
        return output.ToArray();
    }

    /// <summary>A segment inserted after the start-of-image marker.</summary>
    private static byte[] WithSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, 0xD8, 0xFF, marker, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private static readonly byte[] Video = "ftypmp42 the video part"u8.ToArray();

    [Fact]
    public async Task ACaptionChangesOnlyTheXmp_KeepingExifCommentsThePictureAndAMotionPhotosVideo()
    {
        var path = _lib.AddJpeg("motion.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2022, 6, 1) });
        var original = WithSegment(File.ReadAllBytes(path), 0xFE, "A comment from the camera"u8.ToArray());
        File.WriteAllBytes(path, [.. original, .. Video]);
        var before = File.ReadAllBytes(path);

        Assert.Equal(1, (await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), "A dog on a beach.")])).Succeeded);
        var after = File.ReadAllBytes(path);
        Assert.Equal(WithoutXmp(before), WithoutXmp(after));
        Assert.Equal(Video, after[^Video.Length..]);
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(("A dog on a beach.", "A dog on a beach."), (fields.Caption, fields.AltText));
        // Explorer's Title (empty) follows, from the XMP.
        Assert.Equal("A dog on a beach.", await MetadataReader.ReadTitleAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));

        // A second change fits in the room left the first time: the file keeps its size.
        var size = after.Length;
        await XmpFields.WriteAsync(path, "A dog.", null, null);
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(WithoutXmp(before), WithoutXmp(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task ARating_GoesInTheXmp_AndExifsOwnRatingIsPatchedInPlace_SoExplorerReadsIt()
    {
        var path = _lib.AddJpeg("rated.jpg", new TestLibrary.JpegOptions { Rating = 2 });
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. Video]);
        var original = File.ReadAllBytes(path);
        var exif = Segments(original).Where(s => s.Marker == 0xE1 && !IsXmp(original, s)).Select(s => original[s.Offset..(s.Offset + s.Length)]).ToList();

        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 5)).Succeeded);
        Assert.Equal(5, (await MetadataReader.ReadAsync(path)).Rating);
        var after = File.ReadAllBytes(path);
        var exifAfter = Segments(after).Where(s => s.Marker == 0xE1 && !IsXmp(after, s)).Select(s => after[s.Offset..(s.Offset + s.Length)]).ToList();
        Assert.Equal(exif.Select(e => e.Length), exifAfter.Select(e => e.Length));
        // Only the two rating values (two bytes each) may differ.
        Assert.InRange(exif.Zip(exifAfter).Sum(p => p.First.Zip(p.Second).Count(b => b.First != b.Second)), 0, 4);
        Assert.Equal(Video, after[^Video.Length..]);

        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 0)).Succeeded);
        Assert.Equal(0, (await MetadataReader.ReadAsync(path)).Rating);
    }

    [Fact]
    public async Task AMultiPictureFile_WithAnIndexItCantFollow_IsChangedOnlyWhenItKeepsItsSize()
    {
        // An MPF index (Ultra HDR, MPO) records the main picture's size; one that can't be read can't be patched, so
        // the file mustn't grow.
        byte[] mpf = [(byte)'M', (byte)'P', (byte)'F', 0, 0x49, 0x49, 0x2A, 0x00];
        var path = _lib.AddJpeg("hdr.jpg");
        File.WriteAllBytes(path, [.. WithSegment(WithoutRoom(File.ReadAllBytes(path)), 0xE2, mpf), .. "the gain map"u8.ToArray()]);
        var before = File.ReadAllBytes(path);
        var refused = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, refused.Succeeded);
        Assert.Contains("second picture", refused.FailureSummary);
        Assert.Equal(before, File.ReadAllBytes(path));

        // With an XMP that has room (written before the index was added), a change that fits is made.
        var roomy = _lib.AddJpeg("roomy.jpg");
        File.WriteAllBytes(roomy, WithoutRoom(File.ReadAllBytes(roomy)));
        await XmpFields.WriteAsync(roomy, "First.", null, null);
        // The read-back's decoder keeps the file mapped until it's collected, which can take a moment: tried again, as
        // the app's own swap does.
        byte[] withIndex = [.. WithSegment(File.ReadAllBytes(roomy), 0xE2, mpf), .. "the gain map"u8.ToArray()];
        for (var attempt = 1; ; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                File.WriteAllBytes(roomy, withIndex);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
        }
        var size = new FileInfo(roomy).Length;
        await XmpFields.WriteAsync(roomy, "Second.", null, null);
        Assert.Equal(size, new FileInfo(roomy).Length);
        Assert.Equal("Second.", (await XmpFields.ReadAsync(roomy)).Caption);
        Assert.EndsWith("the gain map", File.ReadAllText(roomy));
    }

    /// <summary>A multi-picture file as Ultra HDR and MPO cameras write it: the main JPEG with an MPF index (CIPA
    /// DC-007) at <paramref name="mpfAt"/> among its segments, then a second JPEG. Its two MP Entries give the main
    /// picture's size and where the second starts, counted from the index's header.</summary>
    private static byte[] MultiPicture(byte[] main, byte[] second, bool bigEndian, int mpfAt = 0)
    {
        var mpf = new byte[4 + 8 + 42 + 32];
        "MPF\0"u8.CopyTo(mpf);
        void U16(int at, int v) { if (bigEndian) { mpf[at] = (byte)(v >> 8); mpf[at + 1] = (byte)v; } else { mpf[at] = (byte)v; mpf[at + 1] = (byte)(v >> 8); } }
        void U32(int at, uint v) { for (var i = 0; i < 4; i++) mpf[at + i] = (byte)(v >> (bigEndian ? 8 * (3 - i) : 8 * i)); }
        (bigEndian ? "MM\0*"u8 : "II*\0"u8).CopyTo(mpf.AsSpan(4));
        U32(8, 8);
        U16(12, 3);
        // MPFVersion "0100", NumberOfImages 2, MPEntry: 32 bytes at 50.
        U16(14, 0xB000); U16(16, 7); U32(18, 4); "0100"u8.CopyTo(mpf.AsSpan(22));
        U16(26, 0xB001); U16(28, 4); U32(30, 1); U32(34, 2);
        U16(38, 0xB002); U16(40, 7); U32(42, 32); U32(46, 50);
        // The segment goes in among the main picture's segments, after mpfAt of them.
        var segments = Segments(main);
        var at = mpfAt == 0 ? 2 : segments[mpfAt - 1].Offset + segments[mpfAt - 1].Length;
        var length = mpf.Length + 2;
        byte[] withIndex = [.. main[..at], 0xFF, 0xE2, (byte)(length >> 8), (byte)length, .. mpf, .. main[at..]];
        var header = at + 4 + 4;
        var entries = header + 50;
        void Put(int where, uint v) { for (var i = 0; i < 4; i++) withIndex[where + i] = (byte)(v >> (bigEndian ? 8 * (3 - i) : 8 * i)); }
        Put(entries, 0x030000);
        Put(entries + 4, (uint)withIndex.Length);
        Put(entries + 8, 0);
        Put(entries + 16 + 4, (uint)second.Length);
        Put(entries + 16 + 8, (uint)(withIndex.Length - header));
        return [.. withIndex, .. second];
    }

    /// <summary>The main picture's size and the second's start, from a file's MP Entries.</summary>
    private static (uint Size, int Second) Index(byte[] b)
    {
        var s = Segments(b).First(x => x.Marker == 0xE2 && b.AsSpan(x.Offset + 4).StartsWith("MPF\0"u8));
        var header = s.Offset + 8;
        var big = b[header] == (byte)'M';
        uint U32(int at) => big ? (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]) : (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        var entries = header + 50;
        return (U32(entries + 4), header + (int)U32(entries + 16 + 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUltraHdrPhoto_IsCaptioned_WithItsIndexStillPointingAtBothPictures(bool bigEndian)
    {
        // #90: the change grows the file, and the index's record of the main picture's size grows with it.
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var path = _lib.AddJpeg("hdr.jpg");
        File.WriteAllBytes(path, MultiPicture(WithoutRoom(File.ReadAllBytes(path)), gainMap, bigEndian));
        var before = File.ReadAllBytes(path);
        Assert.Equal((uint)(before.Length - gainMap.Length), Index(before).Size);

        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog on a beach.")).Succeeded);
        var after = File.ReadAllBytes(path);
        Assert.True(after.Length > before.Length);
        var (size, second) = Index(after);
        Assert.Equal((uint)(after.Length - gainMap.Length), size);
        Assert.Equal([0xFF, 0xD9], after[(int)(size - 2)..(int)size]);
        Assert.Equal(gainMap, after[second..]);
        Assert.Equal("A dog on a beach.", (await XmpFields.ReadAsync(path)).Caption);
        // Everything but the XMP and the index's size field is as it was.
        var diff = WithoutXmp(before).Zip(WithoutXmp(after)).Count(p => p.First != p.Second);
        Assert.InRange(diff, 1, 4);
    }

    [Fact]
    public void AnAppleHdrPhoto_WhoseMainSizeRunsLong_IsCaptionedToo()
    {
        // libultrahdr's Apple sample records the main picture 70 bytes too long, into the gain map (apple_gainmap_new.jpg:
        // 44891 for a picture ending at 44821). The gain map's start is exact, so that's what counts; the size is kept in
        // step.
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var jpeg = MultiPicture(WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg"))), gainMap, bigEndian: true);
        var sizeAt = Segments(jpeg).First(x => x.Marker == 0xE2).Offset + 8 + 50 + 4;
        jpeg[sizeAt + 3] += 70;
        var before = Index(jpeg);

        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "A dog on a beach.", AltText = "A dog." });
        var after = Index(changed);
        Assert.Equal(before.Size + (uint)(changed.Length - jpeg.Length), after.Size);
        Assert.Equal(gainMap, changed[after.Second..]);
    }

    [Fact]
    public void AnIndexBeforeTheXmp_HasTheSecondPicturesStartMovedToo()
    {
        // When the XMP comes after the index, the index stays put and the second picture moves away from it.
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>";
        var main = WithSegment(WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg"))), 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)]);
        var jpeg = MultiPicture(main, gainMap, bigEndian: false, mpfAt: 0);
        Assert.True(Segments(jpeg).FindIndex(x => x.Marker == 0xE2) < Segments(jpeg).FindIndex(x => IsXmp(jpeg, x)));
        var before = Index(jpeg);

        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "A dog on a beach.", AltText = "A dog." });
        var delta = changed.Length - jpeg.Length;
        Assert.True(delta > 0);
        var after = Index(changed);
        Assert.Equal(before.Size + (uint)delta, after.Size);
        Assert.Equal(before.Second + delta, after.Second);
        Assert.Equal(gainMap, changed[after.Second..]);
    }

    [Theory]
    [InlineData("type", 40, 2, new byte[] { 4, 0 })]
    [InlineData("count of none", 42, 4, new byte[] { 0, 0, 0, 0 })]
    [InlineData("count not a whole entry", 42, 4, new byte[] { 33, 0, 0, 0 })]
    [InlineData("count too many", 42, 4, new byte[] { 0, 8, 0, 0 })]
    [InlineData("entries past the segment", 46, 4, new byte[] { 0, 1, 0, 0 })]
    [InlineData("IFD past the segment", 8, 4, new byte[] { 0, 1, 0, 0 })]
    [InlineData("no MP Entry tag", 38, 2, new byte[] { 0x05, 0xB0 })]
    [InlineData("main picture not at the start", 62, 4, new byte[] { 1, 0, 0, 0 })]
    [InlineData("main size past the file", 58, 4, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })]
    [InlineData("second picture's start missing", 78, 4, new byte[] { 0, 0, 0, 0 })]
    [InlineData("not a byte order", 4, 2, new byte[] { (byte)'X', (byte)'X' })]
    public void AnIndexItCantFollow_IsRefused_WhenTheFileWouldGrow(string what, int at, int length, byte[] value)
    {
        // Offsets within the MPF segment's payload ("MPF\0" at 0, the header at 4), little-endian, as MultiPicture writes it.
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var jpeg = MultiPicture(WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg"))), gainMap, bigEndian: false);
        var payload = Segments(jpeg).First(x => x.Marker == 0xE2).Offset + 4;
        Assert.Equal(length, value.Length);
        value.CopyTo(jpeg, payload + at);
        Assert.True(Assert.Throws<SecondPictureException>(() => JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "A dog." })) is not null, what);
    }

    [Fact]
    public void AFileWithTwoIndexes_IsntGrown()
    {
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var jpeg = MultiPicture(WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg"))), gainMap, bigEndian: false);
        var first = Segments(jpeg).First(x => x.Marker == 0xE2);
        byte[] twice = [.. jpeg[..(first.Offset + first.Length)], .. jpeg[first.Offset..(first.Offset + first.Length)], .. jpeg[(first.Offset + first.Length)..]];
        Assert.Throws<SecondPictureException>(() => JpegMetadata.Apply(twice, new MetadataChanges { Caption = "A dog." }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheIndexAfterExifAndXmp_AsCamerasWriteIt_IsKeptRight(bool withXmp)
    {
        // Apple's: JFIF, EXIF, then the index, with the new XMP put between EXIF and the index. Pixel's: an XMP already
        // there before the index, replaced and grown. A main size recorded short, as Pixel's are, is kept in step too.
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        // Every APP segment taken out, then EXIF first, as Apple's are.
        var plain = File.ReadAllBytes(_lib.AddJpeg("a.jpg"));
        var appEnd = Segments(plain).TakeWhile(x => x.Marker is >= 0xE0 and <= 0xEF).Select(x => x.Offset + x.Length).DefaultIfEmpty(2).Last();
        byte[] main = [0xFF, 0xD8, .. plain[appEnd..]];
        main = WithSegment(main, 0xE1, [.. "Exif\0\0"u8.ToArray(), 0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        if (withXmp)
        {
            var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\"/></rdf:RDF></x:xmpmeta>";
            var exifEnd = Segments(main)[0].Offset + Segments(main)[0].Length;
            var segment = new List<byte> { 0xFF, 0xE1 };
            byte[] payload = [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)];
            segment.Add((byte)((payload.Length + 2) >> 8));
            segment.Add((byte)(payload.Length + 2));
            segment.AddRange(payload);
            main = [.. main[..exifEnd], .. segment, .. main[exifEnd..]];
        }
        var jpeg = MultiPicture(main, gainMap, bigEndian: false, mpfAt: withXmp ? 2 : 1);
        var sizeAt = Segments(jpeg).First(x => x.Marker == 0xE2).Offset + 8 + 50 + 4;
        jpeg[sizeAt + 1] -= 1;  // 256 bytes short
        var before = Index(jpeg);

        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "A dog on a beach.", AltText = "A dog." });
        var order = Segments(changed).Select(x => x.Marker == 0xE2 ? "MPF" : IsXmp(changed, x) ? "XMP" : x.Marker == 0xE1 ? "EXIF" : $"{x.Marker:X2}").ToList();
        Assert.True(order.IndexOf("XMP") < order.IndexOf("MPF"), string.Join(",", order));
        Assert.True(order.IndexOf("EXIF") < order.IndexOf("XMP"), string.Join(",", order));
        var after = Index(changed);
        Assert.Equal(before.Size + (uint)(changed.Length - jpeg.Length), after.Size);
        Assert.Equal(gainMap, changed[after.Second..]);
        if (withXmp) Assert.Contains("hdrgm:Version=\"1.0\"", System.Text.Encoding.UTF8.GetString(changed));
    }

    [Fact]
    public async Task AnIndexThatDoesntPointAtTheSecondPicture_IsLeftAlone()
    {
        var gainMap = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("gainmap.jpg")));
        var path = _lib.AddJpeg("hdr.jpg");
        var jpeg = MultiPicture(WithoutRoom(File.ReadAllBytes(path)), gainMap, bigEndian: false);
        // The second picture's start, one byte out.
        var entries = Segments(jpeg).First(x => x.Marker == 0xE2).Offset + 8 + 50;
        jpeg[entries + 16 + 8]++;
        File.WriteAllBytes(path, jpeg);
        var refused = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, refused.Succeeded);
        Assert.Contains("second picture", refused.FailureSummary);
        Assert.Equal(jpeg, File.ReadAllBytes(path));
    }

    [Fact]
    public void OtherAppsXmp_IsKept_AndOtherLanguagesToo()
    {
        var xmp = """
            <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:dc="http://purl.org/dc/elements/1.1/" GCamera:MotionPhoto="1" GCamera:MicroVideoOffset="23"><dc:description><rdf:Alt><rdf:li xml:lang="x-default">Old</rdf:li><rdf:li xml:lang="fr">Ancien</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>
            """;
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)]);
        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "New", AltText = "New" });
        var s = Segments(changed).Single(x => IsXmp(changed, x));
        var text = System.Text.Encoding.UTF8.GetString(changed, s.Offset + 4 + XmpSignature.Length, s.Length - 4 - XmpSignature.Length);
        Assert.Contains("GCamera:MotionPhoto=\"1\"", text);
        Assert.Contains("GCamera:MicroVideoOffset=\"23\"", text);
        Assert.Contains(">Ancien<", text);
        Assert.Contains(">New<", text);
        Assert.DoesNotContain(">Old<", text);
        Assert.Equal(plain[2..], changed[(s.Offset + s.Length)..]);
    }

    private static readonly byte[] ExtensionSignature = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();

    /// <summary>A JPEG with Extended XMP, as a Pixel writes it: the main packet naming the extension's GUID (the MD5 of the
    /// extension packet), then the extension in <paramref name="parts"/> segments, each the GUID, the whole length, its
    /// offset and its bytes. <paramref name="extensionBody"/> goes inside the extension's rdf:Description.</summary>
    private byte[] WithExtendedXmp(string extensionBody, int parts = 2, Func<string, string>? guidFor = null, Func<int, (int Offset, int Total)?>? header = null, Func<string, string>? partGuid = null, bool mainNamesIt = true)
    {
        var extension = System.Text.Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
            "xmlns:GDepth=\"http://ns.google.com/photos/1.0/depthmap/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">" +
            extensionBody + "</rdf:Description></rdf:RDF></x:xmpmeta>");
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(extension));
        var guid = guidFor?.Invoke(md5) ?? md5;
        var main = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
                   (mainNamesIt ? $"xmlns:xmpNote=\"http://ns.adobe.com/xmp/note/\" xmpNote:HasExtendedXMP=\"{guid}\"" : "") + "/></rdf:RDF></x:xmpmeta>";
        var jpeg = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("pixel.jpg")));
        var size = (extension.Length + parts - 1) / parts;
        // Added in reverse, as WithSegment puts each first: the main packet ends up first, then the parts in order.
        for (var i = parts - 1; i >= 0; i--)
        {
            var offset = i * size;
            var piece = extension[offset..Math.Min(extension.Length, offset + size)];
            // A test may give a part its own offset and total, or another GUID.
            var (o, total) = header?.Invoke(i) ?? (offset, extension.Length);
            var name = partGuid?.Invoke(guid) ?? guid;
            byte[] head = [.. System.Text.Encoding.ASCII.GetBytes(name), (byte)(total >> 24), (byte)(total >> 16), (byte)(total >> 8), (byte)total,
                (byte)(o >> 24), (byte)(o >> 16), (byte)(o >> 8), (byte)o];
            jpeg = WithSegment(jpeg, 0xE1, [.. ExtensionSignature, .. head, .. piece]);
        }
        return WithSegment(jpeg, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(main)]);
    }

    private static List<byte[]> ExtensionParts(byte[] b) =>
        Segments(b).Where(s => s.Marker == 0xE1 && b.AsSpan(s.Offset + 4).StartsWith(ExtensionSignature)).Select(s => b[s.Offset..(s.Offset + s.Length)]).ToList();

    [Fact]
    public async Task APixelPhotoWithExtendedXmp_IsCaptioned_KeepingTheExtensionByteForByte()
    {
        // #149, as the Mac's #166: a depth map (or gain map details) in the extension, untouched.
        var jpeg = WithExtendedXmp("<GDepth:Data>" + new string('A', 3000) + "</GDepth:Data>");
        var path = _lib.PathOf("pixel-captioned.jpg");
        File.WriteAllBytes(path, jpeg);
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog on a beach.")).Succeeded);
        var after = File.ReadAllBytes(path);
        Assert.Equal(ExtensionParts(jpeg), ExtensionParts(after));
        Assert.Equal("A dog on a beach.", (await XmpFields.ReadAsync(path)).Caption);
        var main = Segments(after).Single(s => IsXmp(after, s));
        var mainBefore = Segments(jpeg).Single(s => IsXmp(jpeg, s));
        string Guid(byte[] b, (byte Marker, int Offset, int Length) s) =>
            System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.UTF8.GetString(b, s.Offset, s.Length), "HasExtendedXMP=\"([0-9A-F]{32})\"").Groups[1].Value;
        Assert.NotEmpty(Guid(after, main));
        Assert.Equal(Guid(jpeg, mainBefore), Guid(after, main));
        // A rating too.
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 4)).Succeeded);
        Assert.Equal(ExtensionParts(jpeg), ExtensionParts(File.ReadAllBytes(path)));
    }

    /// <summary>The length of the extension packet WithExtendedXmp builds around <paramref name="body"/>.</summary>
    private static int ExtensionLength(string body) => System.Text.Encoding.UTF8.GetByteCount(
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
        "xmlns:GDepth=\"http://ns.google.com/photos/1.0/depthmap/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">" +
        body + "</rdf:Description></rdf:RDF></x:xmpmeta>");

    /// <summary>The file with an extra extension part holding only its header: the GUID, total and offset, no bytes.</summary>
    private static byte[] WithEmptyPart(byte[] jpeg)
    {
        var part = Segments(jpeg).First(s => s.Marker == 0xE1 && jpeg.AsSpan(s.Offset + 4).StartsWith(ExtensionSignature));
        var headerOnly = jpeg[(part.Offset + 4)..(part.Offset + 4 + ExtensionSignature.Length + 40)];
        return WithSegment(jpeg, 0xE1, headerOnly);
    }

    /// <summary>A Pixel-style file whose extension holds dc:description as an attribute.</summary>
    private byte[] WithExtendedXmpAttribute()
    {
        var jpeg = WithExtendedXmp("<GDepth:Data>x</GDepth:Data>");
        // Rebuilt by hand: the extension's rdf:Description carries dc:description="Old".
        var extension = System.Text.Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:description=\"Old\"/></rdf:RDF></x:xmpmeta>");
        var guid = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(extension));
        var main = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
                   $"xmlns:xmpNote=\"http://ns.adobe.com/xmp/note/\" xmpNote:HasExtendedXMP=\"{guid}\"/></rdf:RDF></x:xmpmeta>";
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("pixel-attribute.jpg")));
        byte[] head = [.. System.Text.Encoding.ASCII.GetBytes(guid), (byte)(extension.Length >> 24), (byte)(extension.Length >> 16), (byte)(extension.Length >> 8), (byte)extension.Length, 0, 0, 0, 0];
        var withPart = WithSegment(plain, 0xE1, [.. ExtensionSignature, .. head, .. extension]);
        return WithSegment(withPart, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(main)]);
    }

    public static TheoryData<string> BadExtensions() =>
    [
        "guid", "missing part", "caption inside", "rating inside", "no guid in main", "overlap", "different totals", "huge total",
        "another guid in a part", "lowercase guid", "caption as attribute", "title inside", "main without the note", "empty part",
    ];

    [Theory]
    [MemberData(nameof(BadExtensions))]
    public void ExtendedXmpThatDoesntAddUp_OrHoldsWhatWouldChange_IsRefused(string what)
    {
        const string body = "<GDepth:Data>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA</GDepth:Data>";
        var jpeg = what switch
        {
            "guid" => WithExtendedXmp("<GDepth:Data>x</GDepth:Data>", guidFor: md5 => new string('0', 32)),
            "caption inside" => WithExtendedXmp("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">Old</rdf:li></rdf:Alt></dc:description>"),
            "caption as attribute" => WithExtendedXmpAttribute(),
            "title inside" => WithExtendedXmp("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Old</rdf:li></rdf:Alt></dc:title>"),
            "rating inside" => WithExtendedXmp("<xmp:Rating>3</xmp:Rating>"),
            // The second part says it starts 10 bytes in, over the first.
            "overlap" => WithExtendedXmp(body, header: i => (i == 1 ? 10 : 0, ExtensionLength(body))),
            "different totals" => WithExtendedXmp(body, header: i => (i * ((ExtensionLength(body) + 1) / 2), ExtensionLength(body) + i)),
            "huge total" => WithExtendedXmp(body, header: i => (i * ((ExtensionLength(body) + 1) / 2), int.MaxValue)),
            "another guid in a part" => WithExtendedXmp(body, partGuid: g => new string('F', 32)),
            "lowercase guid" => WithExtendedXmp(body, guidFor: md5 => md5.ToLowerInvariant()),
            "main without the note" => WithExtendedXmp(body, mainNamesIt: false),
            "empty part" => WithExtendedXmp(body, parts: 2, header: i => (i * ((ExtensionLength(body) + 1) / 2), ExtensionLength(body))) is var j ? WithEmptyPart(j) : j,
            _ => WithExtendedXmp("<GDepth:Data>" + new string('A', 3000) + "</GDepth:Data>", parts: 3),
        };
        if (what == "missing part")
        {
            var part = Segments(jpeg).First(s => s.Marker == 0xE1 && jpeg.AsSpan(s.Offset + 4).StartsWith(ExtensionSignature));
            jpeg = [.. jpeg[..part.Offset], .. jpeg[(part.Offset + part.Length)..]];
        }
        if (what == "no guid in main")
        {
            var main = Segments(jpeg).Single(s => IsXmp(jpeg, s));
            jpeg = [.. jpeg[..main.Offset], .. jpeg[(main.Offset + main.Length)..]];
        }
        var changes = what switch
        {
            "rating inside" => new MetadataChanges { Stars = 4 },
            "title inside" => new MetadataChanges { Title = "A dog." },
            _ => new MetadataChanges { Caption = "A dog." },
        };
        var refused = Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(jpeg, changes));
        Assert.StartsWith("Its metadata is spread over several parts (Extended XMP)", refused.Message);
        if (what is "caption inside" or "caption as attribute") Assert.EndsWith("and its caption is kept in another part.", refused.Message);
        else if (what is not ("title inside" or "rating inside")) Assert.EndsWith("that don't add up.", refused.Message);
        if (what == "title inside") Assert.EndsWith("and its title is kept in another part.", refused.Message);
        if (what == "rating inside") Assert.EndsWith("and its rating is kept in another part.", refused.Message);
        // What isn't in the extension can still change when the caption is inside it.
        if (what == "caption inside") Assert.NotNull(JpegMetadata.Apply(jpeg, new MetadataChanges { Stars = 2 }));
    }

    /// <summary>A JPEG with Photoshop's IPTC (APP13): a caption (2:120) and, unless <paramref name="digest"/> is "none",
    /// the IPTC digest: right ("right"), stale ("stale", as when an older app changed the IPTC afterwards) or 15 bytes
    /// ("short"). The IPTC resource can have a name, a junk resource with a huge size can come first, and the resources can
    /// be split across two APP13 segments, each starting with Photoshop's header, as Photoshop writes a long list.</summary>
    private byte[] WithPhotoshopIptc(string caption, string digest, string name = "", bool hugeFirst = false, bool split = false)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(caption);
        byte[] iptc = [0x1C, 2, 0, 0, 2, 0, 4, 0x1C, 2, 120, (byte)(text.Length >> 8), (byte)text.Length, .. text];
        static byte[] Resource(int id, byte[] data, string name = "", uint? size = null)
        {
            var n = System.Text.Encoding.ASCII.GetBytes(name);
            byte[] pascal = [(byte)n.Length, .. n, .. n.Length % 2 == 0 ? new byte[] { 0 } : []];
            var length = size ?? (uint)data.Length;
            return [.. "8BIM"u8.ToArray(), (byte)(id >> 8), (byte)id, .. pascal, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
                .. data, .. data.Length % 2 == 1 ? new byte[] { 0 } : []];
        }
        var md5 = System.Security.Cryptography.MD5.HashData(digest == "stale" ? [.. iptc, 0] : iptc);
        byte[] resources = [.. hugeFirst ? Resource(0x0400, [1, 2], size: 0x7FFFFFF0) : [], .. Resource(0x0404, iptc, name),
            .. digest switch { "none" => [], "short" => Resource(0x0425, md5[..15]), _ => Resource(0x0425, md5) }];
        var photoshop = "Photoshop 3.0\0"u8.ToArray();
        var jpeg = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("iptc.jpg")));
        if (!split) return WithSegment(jpeg, 0xED, [.. photoshop, .. resources]);
        // Split inside the IPTC resource; WithSegment puts each first, so the second half goes in first.
        var half = resources.Length / 2;
        return WithSegment(WithSegment(jpeg, 0xED, [.. photoshop, .. resources[half..]]), 0xED, [.. photoshop, .. resources[..half]]);
    }

    [Fact]
    public async Task TheIptcCaptionOtherAppsRead_IsTheCaptionShown_WhenItWins()
    {
        // #181: Get Info and the list show the caption everyone else sees.
        var stale = _lib.PathOf("stale-shown.jpg");
        File.WriteAllBytes(stale, WithPhotoshopIptc("An old caption", "stale"));
        Assert.Equal(new XmpFields.Fields("An old caption", "", "", HasCaption: true), await XmpFields.ReadAsync(stale));
        Assert.Equal("An old caption", (await MetadataReader.ReadAsync(stale)).Caption);
        // In step: the XMP's (none here) is read, as other apps do.
        var right = _lib.PathOf("right-shown.jpg");
        File.WriteAllBytes(right, WithPhotoshopIptc("An old caption", "right"));
        Assert.False((await XmpFields.ReadAsync(right)).HasCaption);
        // Split across two segments too.
        var split = _lib.PathOf("split-shown.jpg");
        File.WriteAllBytes(split, WithPhotoshopIptc("An old caption", "stale", split: true));
        Assert.Equal("An old caption", (await XmpFields.ReadAsync(split)).Caption);
    }

    [Fact]
    public async Task TheSameCaptionWithCRLineBreaks_IsSaved_AndReadsBack()
    {
        // Photoshop writes CR; the description has LF. Allowed (it's the same caption), and the read-back agrees.
        var path = _lib.PathOf("cr.jpg");
        File.WriteAllBytes(path, WithPhotoshopIptc("Line one\rLine two", "stale"));
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "Line one\nLine two")).Succeeded);
        Assert.Equal("Line one\nLine two", (await XmpFields.ReadAsync(path)).Caption);
    }

    [Fact]
    public async Task ARefusal_IsKnownBeforeAnythingIsAsked_AndAnEmptyIptcCaptionShowsTheXmps()
    {
        var stale = _lib.PathOf("refuse.jpg");
        File.WriteAllBytes(stale, WithPhotoshopIptc("An old caption", "stale"));
        Assert.True(await XmpFields.WouldRefuseCaptionAsync(stale, "A dog."));
        Assert.False(await XmpFields.WouldRefuseCaptionAsync(stale, "An old caption"));
        var right = _lib.PathOf("fine.jpg");
        File.WriteAllBytes(right, WithPhotoshopIptc("An old caption", "right"));
        Assert.False(await XmpFields.WouldRefuseCaptionAsync(right, "A dog."));
        // A present but empty IPTC caption isn't shown in place of the XMP's, as on the Mac, but a new caption would
        // still be refused, so Edit Caption says so before opening.
        var emptyFile = _lib.PathOf("empty-iptc.jpg");
        File.WriteAllBytes(emptyFile, WithPhotoshopIptc("", "stale"));
        using (var empty = File.OpenRead(emptyFile)) Assert.Null(JpegMetadata.IptcCaptionReadFirst(empty));
        Assert.True(await XmpFields.HasOlderCaptionAsync(emptyFile));
        Assert.True(await XmpFields.WouldRefuseCaptionAsync(emptyFile, "A dog."));
        Assert.True(await XmpFields.HasOlderCaptionAsync(stale));
        Assert.False(await XmpFields.HasOlderCaptionAsync(right));
    }

    public static TheoryData<string> OddJpegs() => ["fill bytes", "not a jpeg", "not photoshop", "truncated", "past the end", "after the picture"];

    [Theory]
    [MemberData(nameof(OddJpegs))]
    public void TheIptcWalk_FollowsOddFiles_WithoutFailing(string what)
    {
        var good = WithPhotoshopIptc("An old caption", "stale");
        var app13 = Segments(good).Single(x => x.Marker == 0xED);
        byte[] file = what switch
        {
            // Fill bytes before a marker are allowed; the caption is still found.
            "fill bytes" => [0xFF, 0xD8, 0xFF, 0xFF, .. good[2..]],
            "not a jpeg" => "GIF89a not a jpeg at all"u8.ToArray(),
            "not photoshop" => WithSegment(WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("odd.jpg"))), 0xED, "Something else\0 8BIM"u8.ToArray()),
            "truncated" => good[..(app13.Offset + 10)],
            "past the end" => [.. good[..(app13.Offset + 2)], 0xFF, 0xF0, .. good[(app13.Offset + 4)..(app13.Offset + 40)]],
            // Photoshop's segment only after the picture starts: not read.
            _ => [.. WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("plain.jpg")))[..^2], .. good[app13.Offset..(app13.Offset + app13.Length)], 0xFF, 0xD9],
        };
        using var stream = new MemoryStream(file);
        var caption = JpegMetadata.IptcCaptionReadFirst(stream);
        if (what == "fill bytes") Assert.Equal("An old caption", caption);
        else Assert.Null(caption);
    }

    [Fact]
    public async Task AnOlderIptcCaptionReadersWouldShowInstead_IsntHidden_AndSaysSo()
    {
        // #169, as the Mac: the IPTC was changed after its digest was written, so readers show its caption, not the XMP's.
        var path = _lib.PathOf("stale.jpg");
        File.WriteAllBytes(path, WithPhotoshopIptc("An old caption", "stale"));
        var before = File.ReadAllBytes(path);
        var refused = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog on a beach.");
        Assert.Equal(0, refused.Succeeded);
        Assert.Equal("stale.jpg: An older caption saved in it by Photoshop or a similar app would still be read instead of the new one, so it was left as it is.",
            refused.FailureSummary);
        Assert.Equal(before, File.ReadAllBytes(path));
        // A rating isn't the caption, so it can still change.
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 3)).Succeeded);
    }

    [Theory]
    [InlineData("odd length", true)]
    [InlineData("named", true)]
    [InlineData("split", true)]
    [InlineData("short digest", true)]
    [InlineData("cleared", true)]
    [InlineData("right", false)]
    [InlineData("none", false)]
    [InlineData("same caption", false)]
    [InlineData("same with CR", false)]
    // A resource with an impossible size stops the walk, as ExifTool's does: nothing found, no error, the caption written.
    [InlineData("huge size first", false)]
    public void AnOlderIptcCaption_IsFound_HoweverItsWritten(string what, bool refused)
    {
        var jpeg = what switch
        {
            "odd length" => WithPhotoshopIptc("An old caption!", "stale"),
            "named" => WithPhotoshopIptc("An old caption", "stale", name: "IPTC"),
            "split" => WithPhotoshopIptc("An old caption", "stale", split: true),
            "short digest" => WithPhotoshopIptc("An old caption", "short"),
            "same with CR" => WithPhotoshopIptc("Line one\rLine two", "stale"),
            "huge size first" => WithPhotoshopIptc("An old caption", "stale", hugeFirst: true),
            "right" => WithPhotoshopIptc("An old caption", "right"),
            "none" => WithPhotoshopIptc("An old caption", "none"),
            _ => WithPhotoshopIptc("An old caption", "stale"),
        };
        var caption = what switch
        {
            "same caption" => "An old caption",
            "same with CR" => "Line one\nLine two",
            "cleared" => "",
            _ => "A dog on a beach.",
        };
        if (refused)
        {
            Assert.Throws<OlderCaptionException>(() => JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = caption }));
            return;
        }
        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = caption });
        // Photoshop's segments are copied as they were, and the XMP has the new caption.
        Assert.Equal(Segments(jpeg).Where(x => x.Marker == 0xED).Select(x => jpeg[x.Offset..(x.Offset + x.Length)]),
            Segments(changed).Where(x => x.Marker == 0xED).Select(x => changed[x.Offset..(x.Offset + x.Length)]));
        var xmp = Segments(changed).Single(x => IsXmp(changed, x));
        Assert.Contains($">{caption}<", System.Text.Encoding.UTF8.GetString(changed, xmp.Offset, xmp.Length));
    }

    [Fact]
    public void XmpItCantRead_OrExtendedXmp_IsLeftAlone()
    {
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var broken = WithSegment(plain, 0xE1, [.. XmpSignature, .. "<x:xmpmeta not xml"u8.ToArray()]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(broken, new MetadataChanges { Caption = "A" }));
        // An extension part too short to hold its own header, with no main packet naming it.
        var extended = WithSegment(plain, 0xE1, [.. "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), 1, 2, 3]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(extended, new MetadataChanges { Caption = "A" }));
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply("not a jpeg"u8.ToArray(), new MetadataChanges { Caption = "A" }));
    }

    [Fact]
    public async Task AnAccentedTitleInExif_IsEmptiedWhenTheCaptionReplacesIt()
    {
        var path = _lib.AddJpeg("cafe.jpg");
        // Windows' own handler writes the Title into EXIF (ImageDescription as UTF-8, and XPTitle).
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await file.Properties.SavePropertiesAsync([new KeyValuePair<string, object?>("System.Title", "Café au lait")]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "Un café.")).Succeeded);
        Assert.Equal("Un café.", await MetadataReader.ReadTitleAsync(path));
        var check = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var subject = await check.Properties.RetrievePropertiesAsync(["System.Subject"]);
        Assert.NotEqual("Café au lait", subject.TryGetValue("System.Subject", out var value) ? value as string : null);
        // Nowhere in the file any more: not in EXIF's ImageDescription, nor its XPTitle (UTF-16), nor the XMP.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf("Café au lait"u8) < 0, "The old title is still in the file as UTF-8.");
        Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("Café au lait")) < 0, "The old title is still in XPTitle.");
    }

    [Fact]
    public async Task ARefusal_SaysWhy()
    {
        var path = _lib.AddJpeg("ext.jpg");
        File.WriteAllBytes(path, WithSegment(WithoutRoom(File.ReadAllBytes(path)), 0xE1, [.. "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), 1, 2, 3]));
        var result = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("ext.jpg: Its metadata is spread over several parts (Extended XMP) that don't add up. It was left as it is.", result.FailureSummary);
    }

    [Fact]
    public void XmpThatIsntUtf8_IsLeftAlone_RatherThanChanged()
    {
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        byte[] latin = [.. "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:my=\"urn:my\" my:Name=\"Caf"u8.ToArray(), 0xE9, .. "\"/></rdf:RDF></x:xmpmeta>"u8.ToArray()];
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. latin]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "A" }));
    }

    [Fact]
    public void ACaptionInOneLanguage_IsReplacedThere_AndLineBreaksStayEscaped()
    {
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:my=\"urn:my\"><my:Place>Kona&#xD;&#xA;HI</my:Place><dc:description><rdf:Alt><rdf:li xml:lang=\"en-US\">Old</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>";
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)]);
        var changed = JpegMetadata.Apply(jpeg, new MetadataChanges { Caption = "New" });
        var seg = Segments(changed).Single(x => IsXmp(changed, x));
        var text = System.Text.Encoding.UTF8.GetString(changed, seg.Offset + 4 + XmpSignature.Length, seg.Length - 4 - XmpSignature.Length);
        Assert.DoesNotContain(">Old<", text);
        Assert.Contains("xml:lang=\"en-US\">New<", text);
        Assert.Contains("Kona&#xD;", text);
    }

    [Fact]
    public async Task APhotoChangedWhileWaitingToSwapIn_IsntReplacedWithTheOldCopy()
    {
        var path = _lib.AddJpeg("busy.jpg");
        // OneDrive has the photo open (so the swap waits), then writes a newer version.
        var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var oneDrive = Task.Run(async () =>
        {
            await Task.Delay(250);
            held.Write(" newer"u8);
            held.Dispose();
        });
        await Assert.ThrowsAsync<IOException>(() => XmpFields.WriteAsync(path, "A dog.", null, null));
        await oneDrive;
        Assert.EndsWith(" newer", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task APhotosCreationTime_SurvivesASave()
    {
        var path = _lib.AddJpeg("dated.jpg");
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 4)).Succeeded);
        Assert.Equal(created, File.GetCreationTimeUtc(path));
    }

    // MARK: PNGs changed chunk by chunk (#82)

    /// <summary>The PNG's chunks (type, offset, total length), up to and including IEND.</summary>
    private static List<(string Type, int Offset, int Length)> PngChunks(byte[] b)
    {
        var list = new List<(string, int, int)>();
        var at = 8;
        while (at + 12 <= b.Length)
        {
            var length = (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(b, at + 4, 4);
            list.Add((type, at, length + 12));
            at += length + 12;
            if (type == "IEND") break;
        }
        return list;
    }

    private static bool IsXmpChunk(byte[] b, (string Type, int Offset, int Length) c) =>
        c.Type == "iTXt" && b.AsSpan(c.Offset + 8).StartsWith("XML:com.adobe.xmp\0"u8);

    /// <summary>The file without its XMP chunk: everything My Photo Library Described must leave as it was.</summary>
    private static byte[] WithoutXmpChunk(byte[] b)
    {
        var output = new List<byte>(b[..8]);
        var chunks = PngChunks(b);
        foreach (var c in chunks.Where(c => !IsXmpChunk(b, c))) output.AddRange(b[c.Offset..(c.Offset + c.Length)]);
        var end = chunks[^1].Offset + chunks[^1].Length;
        output.AddRange(b[end..]);
        return output.ToArray();
    }

    /// <summary>A chunk inserted after IHDR.</summary>
    private static byte[] WithChunk(byte[] png, string type, byte[] data)
    {
        var ihdrEnd = 8 + 25;
        var chunk = new byte[12 + data.Length];
        chunk[0] = (byte)(data.Length >> 24); chunk[1] = (byte)(data.Length >> 16); chunk[2] = (byte)(data.Length >> 8); chunk[3] = (byte)data.Length;
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = PngMetadata.Crc32(chunk.AsSpan(4, 4 + data.Length));
        chunk[^4] = (byte)(crc >> 24); chunk[^3] = (byte)(crc >> 16); chunk[^2] = (byte)(crc >> 8); chunk[^1] = (byte)crc;
        return [.. png[..ihdrEnd], .. chunk, .. png[ihdrEnd..]];
    }

    [Fact]
    public async Task APngGetsItsCaptionAndAccessibilityFields_InItsXmpChunkOnly()
    {
        var path = _lib.AddPng("screenshot.png");
        var original = WithChunk(File.ReadAllBytes(path), "tEXt", "Software\0Snipping Tool"u8.ToArray());
        File.WriteAllBytes(path, [.. original, .. "after the end"u8.ToArray()]);
        var before = File.ReadAllBytes(path);

        Assert.Equal(1, (await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), "A settings window.")])).Succeeded);
        var after = File.ReadAllBytes(path);
        Assert.Equal(WithoutXmpChunk(before), WithoutXmpChunk(after));
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(("A settings window.", "A settings window.", true), (fields.Caption, fields.AltText, fields.HasCaption));
        Assert.Equal("A settings window.", (await MetadataReader.ReadAsync(path)).Caption);
        // The XMP goes before the picture, where readers look first.
        var chunks = PngChunks(after);
        Assert.True(chunks.FindIndex(c => IsXmpChunk(after, c)) < chunks.FindIndex(c => c.Type == "IDAT"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));

        // Changed again: still one XMP chunk.
        await XmpFields.WriteAsync(path, "A dialog.", null, null);
        var again = File.ReadAllBytes(path);
        Assert.Single(PngChunks(again), c => IsXmpChunk(again, c));
        Assert.Equal("A dialog.", (await XmpFields.ReadAsync(path)).Caption);
    }

    [Fact]
    public async Task APngsTitle_FollowsTheCaption_UnlessSomeoneWroteADifferentOne()
    {
        // Windows keeps a PNG's Title in its XMP (dc:title): written there the old way, as Explorer would.
        var path = _lib.AddPng("old.png");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await file.Properties.SavePropertiesAsync([new KeyValuePair<string, object?>("System.Title", "The old caption")]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal("The old caption", (await MetadataReader.ReadAsync(path)).Caption);
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "The new caption")).Succeeded);
        Assert.Equal("The new caption", (await MetadataReader.ReadAsync(path)).Caption);
        Assert.Equal("The new caption", await MetadataReader.ReadTitleAsync(path));

        var mine = _lib.AddPng("mine.png");
        var mineFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(mine);
        await mineFile.Properties.SavePropertiesAsync([new KeyValuePair<string, object?>("System.Title", "A title of my own")]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await XmpFields.WriteAsync(mine, "An old caption", null, null);
        await MetadataWriter.SetCaptionAsync([ItemFor(mine)], "A new caption");
        Assert.Equal("A new caption", (await XmpFields.ReadAsync(mine)).Caption);
        Assert.Equal("A title of my own", await MetadataReader.ReadTitleAsync(mine));
    }

    [Fact]
    public void ACompressedXmpChunk_IsRead_AndADamagedPng_IsLeftAlone()
    {
        var png = File.ReadAllBytes(_lib.AddPng("a.png"));
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">Packed</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>"u8.ToArray();
        var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(xmp);
        var compressed = WithChunk(png, "iTXt", [.. "XML:com.adobe.xmp\0"u8.ToArray(), 1, 0, 0, 0, .. packed.ToArray()]);
        Assert.Equal("Packed", PngMetadata.Fields(compressed).Caption);
        var changed = PngMetadata.Apply(compressed, new MetadataChanges { Caption = "Unpacked" });
        Assert.Equal("Unpacked", PngMetadata.Fields(changed).Caption);

        var damaged = (byte[])png.Clone();
        damaged[20] ^= 0xFF;  // inside IHDR: its CRC no longer matches
        Assert.Throws<MetadataRefusedException>(() => PngMetadata.Apply(damaged, new MetadataChanges { Caption = "A" }));
    }

    /// <summary>EXIF (little-endian TIFF) with only an ImageDescription, as every Mac screenshot carries.</summary>
    private static byte[] ExifWithDescription(string text)
    {
        var value = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        var exif = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0 };
        exif.AddRange([0x0E, 0x01, 2, 0, (byte)value.Length, 0, 0, 0, 26, 0, 0, 0]);
        exif.AddRange([0, 0, 0, 0]);
        exif.AddRange(value);
        return exif.ToArray();
    }

    [Fact]
    public async Task APngsRating_GoesInItsXmp_AndExplorerReadsIt_WithNothingElseChanged()
    {
        var path = _lib.AddPng("rated.png");
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. "after the end"u8.ToArray()]);
        var before = File.ReadAllBytes(path);
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 4)).Succeeded);
        Assert.Equal(4, (await MetadataReader.ReadAsync(path)).Rating);
        Assert.Equal(WithoutXmpChunk(before), WithoutXmpChunk(File.ReadAllBytes(path)));
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 0)).Succeeded);
        Assert.Equal(0, (await MetadataReader.ReadAsync(path)).Rating);
    }

    [Fact]
    public void AScreenshotsDescription_IsEmptiedInPlace_WhenTheCaptionChanges()
    {
        var png = WithChunk(File.ReadAllBytes(_lib.AddPng("screenshot.png")), "eXIf", ExifWithDescription("Screenshot"));
        var changed = PngMetadata.Apply(png, new MetadataChanges { Caption = "A settings window." });
        var exif = PngChunks(changed).Single(c => c.Type == "eXIf");
        Assert.Equal(PngChunks(png).Single(c => c.Type == "eXIf").Length, exif.Length);
        Assert.True(changed.AsSpan().IndexOf("Screenshot"u8) < 0, "The screenshot's description is still there.");
        // Its CRC is right: the file still reads.
        Assert.Equal("A settings window.", PngMetadata.Fields(changed).Caption);
        // A rating alone leaves it as it was.
        var rated = PngMetadata.Apply(png, new MetadataChanges { Stars = 3 });
        Assert.True(rated.AsSpan().IndexOf("Screenshot"u8) >= 0);
    }

    [Fact]
    public void XmpAfterThePicture_MovesBeforeIt_AndTwoBecomeOne_AndAnAnimationsFramesComeAfterIt()
    {
        var plain = File.ReadAllBytes(_lib.AddPng("a.png"));
        byte[] Xmp(string caption) => [.. "XML:com.adobe.xmp\0"u8.ToArray(), 0, 0, 0, 0, .. System.Text.Encoding.UTF8.GetBytes(
            $"<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:description><rdf:Alt><rdf:li xml:lang='x-default'>{caption}</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>")];
        // Two XMP chunks, after IHDR, and an animation control chunk before the picture.
        var animated = WithChunk(WithChunk(WithChunk(plain, "acTL", [0, 0, 0, 1, 0, 0, 0, 0]), "iTXt", Xmp("Second")), "iTXt", Xmp("First"));
        var changed = PngMetadata.Apply(animated, new MetadataChanges { Caption = "New" });
        var chunks = PngChunks(changed);
        Assert.Single(chunks, c => IsXmpChunk(changed, c));
        Assert.True(chunks.FindIndex(c => IsXmpChunk(changed, c)) < chunks.FindIndex(c => c.Type == "acTL"));
        Assert.Equal("New", PngMetadata.Fields(changed).Caption);
    }

    [Fact]
    public void AnXmpChunkThatInflatesHuge_IsRefused_RatherThanFillingMemory()
    {
        var png = File.ReadAllBytes(_lib.AddPng("bomb.png"));
        var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(new byte[PngMetadata.MaxInflated + 1024]);
        var bomb = WithChunk(png, "iTXt", [.. "XML:com.adobe.xmp\0"u8.ToArray(), 1, 0, 0, 0, .. packed.ToArray()]);
        Assert.Throws<MetadataRefusedException>(() => PngMetadata.Fields(bomb));
    }

    [Fact]
    public async Task APngThatCantBeFollowed_IsReadForItsTitleOnce_AndAJpegCalledPngSaysWhatItIs()
    {
        var damaged = _lib.AddPng("damaged.png");
        var bytes = File.ReadAllBytes(damaged);
        bytes[20] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);
        // Stamped as read, so it isn't read again on every scan.
        Assert.Equal(Models.MediaMetadata.CurrentVersion, (await MetadataReader.ReadAsync(damaged)).Version);

        var jpeg = _lib.AddJpeg("photo.jpg");
        var named = Path.ChangeExtension(jpeg, ".png");
        File.Move(jpeg, named);
        Assert.Equal(Models.MediaMetadata.CurrentVersion, (await MetadataReader.ReadAsync(named)).Version);
        var result = await MetadataWriter.SetCaptionAsync([ItemFor(named)], "A dog.");
        Assert.Equal(0, result.Succeeded);
        Assert.Contains("isn't the kind of file its name says", result.FailureSummary);
    }

    [Fact]
    public async Task APngCalledJpg_IsLeftAlone_Saying_Why()
    {
        var path = _lib.AddPng("fake.png");
        var renamed = Path.ChangeExtension(path, ".jpg");
        File.Move(path, renamed);
        var before = File.ReadAllBytes(renamed);
        var result = await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(renamed), "A dog.")]);
        Assert.Equal(0, result.Succeeded);
        Assert.Contains("isn't the kind of file", result.FailureSummary);
        Assert.Equal(before, File.ReadAllBytes(renamed));
    }

    [Fact]
    public async Task ACaptionClearedInTheFile_DoesntMakeSomeonesTitleTheOldCaption()
    {
        var path = _lib.AddJpeg("titled.jpg", new TestLibrary.JpegOptions { Title = "My title" });
        await XmpFields.WriteAsync(path, "", null, null);
        await MetadataWriter.SetCaptionAsync([ItemFor(path)], "New");
        Assert.Equal("New", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
    }

    [Fact]
    public async Task ALongDescription_GoesInAsShortAltTextAndAnExtendedDescription_ClearedWhenNoLongerNeeded()
    {
        var path = _lib.AddJpeg("long.jpg");
        var first = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. ";
        var rest = "Waves break behind it and two people walk in the distance near the dunes. The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        var description = first + rest;
        Assert.Equal(1, (await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), description)])).Succeeded);
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(FileFields.AltText(description), fields.AltText);
        Assert.True(fields.AltText.Length <= 250);
        Assert.Equal(description.Trim(), fields.ExtendedDescription);

        await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), "A dog.")]);
        fields = await XmpFields.ReadAsync(path);
        Assert.Equal("A dog.", fields.AltText);
        Assert.Equal("", fields.ExtendedDescription);
    }

    [Fact]
    public async Task ACaptionKeptTheOldWay_InTheTitle_IsFollowedByTheTitleWhenReplaced()
    {
        // My Photo Library Described before #62, and Explorer, kept the caption in the Title (EXIF and XP title too).
        var path = _lib.AddJpeg("old.jpg", new TestLibrary.JpegOptions { Title = "The old caption" });
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "The new caption")).Succeeded);
        Assert.Equal("The new caption", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("The new caption", await MetadataReader.ReadTitleAsync(path));
    }

    [Fact]
    public async Task FieldsThatCantBeReadNow_AreAnError_WhenDecidingWhatToWrite()
    {
        var path = _lib.AddJpeg("locked.jpg");
        await XmpFields.WriteAsync(path, null, "Another app's alt text.", null);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(() => MetadataReader.ReadAsync(path, forWriting: true));
        Assert.Equal("Another app's alt text.", (await MetadataReader.ReadAsync(path, forWriting: true)).AltText);
    }


    [Fact]
    public async Task EditingACaption_KeepsExplorersTitleInStep_UnlessSomeoneWroteADifferentTitle()
    {
        var plain = _lib.AddJpeg("plain.jpg");
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(plain)], "First")).Succeeded);
        Assert.Equal("First", (await XmpFields.ReadAsync(plain)).Caption);
        Assert.Equal("First", await MetadataReader.ReadTitleAsync(plain));
        await MetadataWriter.SetCaptionAsync([ItemFor(plain)], "Second");
        Assert.Equal("Second", await MetadataReader.ReadTitleAsync(plain));

        var titled = _lib.AddJpeg("titled.jpg", new TestLibrary.JpegOptions { Title = "A title of my own" });
        await XmpFields.WriteAsync(titled, "An old caption", null, null);
        await MetadataWriter.SetCaptionAsync([ItemFor(titled)], "A new caption");
        Assert.Equal("A new caption", (await XmpFields.ReadAsync(titled)).Caption);
        Assert.Equal("A title of my own", await MetadataReader.ReadTitleAsync(titled));
    }

    [Fact]
    public void JpegPngAndTiff_HoldTheFields()
    {
        Assert.True(XmpFields.CanWrite(@"C:\a.JPG"));
        Assert.True(XmpFields.CanWrite(@"C:\a.tiff"));
        Assert.True(XmpFields.CanWrite(@"C:\a.png"));
        Assert.True(XmpFields.CanWrite(@"C:\a.heic"));
        Assert.True(XmpFields.EditsInPlace(@"C:\a.HEIF"));
        Assert.False(XmpFields.CanWrite(@"C:\a.gif"));
        Assert.False(XmpFields.CanWrite(@"C:\a.mp4"));
    }

    private static Models.MediaItem ItemFor(string path)
    {
        var info = new FileInfo(path);
        return new Models.MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime);
    }
}
