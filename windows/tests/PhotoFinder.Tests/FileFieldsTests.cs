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

    [Fact]
    public void AJpegHoldingASecondPicture_IsRecognised()
    {
        var plain = File.ReadAllBytes(_lib.AddJpeg("plain.jpg"));
        Assert.False(XmpFields.HoldsSecondPicture(plain));
        // Zeros after the end are only padding.
        Assert.False(XmpFields.HoldsSecondPicture([.. plain, 0, 0, 0, 0]));
        // A motion photo's video after the end.
        Assert.True(XmpFields.HoldsSecondPicture([.. plain, .. "ftypmp42 video"u8.ToArray()]));
        // A Multi-Picture Format index (Ultra HDR, MPO), even before the second picture is reached.
        byte[] mpf = [0xFF, 0xE2, 0x00, 0x08, (byte)'M', (byte)'P', (byte)'F', 0x00, 0x49, 0x49];
        Assert.True(XmpFields.HoldsSecondPicture([0xFF, 0xD8, .. mpf, .. plain[2..]]));
        Assert.False(XmpFields.HoldsSecondPicture("not a jpeg"u8.ToArray()));
    }

    [Fact]
    public async Task AMotionPhoto_IsLeftExactlyAsItWas_WithAReason()
    {
        var path = _lib.AddJpeg("motion.jpg");
        File.AppendAllText(path, "ftypmp42 the video part");
        var before = File.ReadAllBytes(path);
        var caption = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, caption.Succeeded);
        Assert.Contains("second picture", caption.FailureSummary);
        var rating = await MetadataWriter.SetRatingAsync([ItemFor(path)], 5);
        Assert.Equal(0, rating.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(path));
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
        // Photo Finder before #62, and Explorer, kept the caption in the Title (EXIF and XP title too).
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
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            // Only reading allowed: the property system still reads, WIC's read-write-sharing open doesn't.
            var shown = await MetadataReader.ReadAsync(path);
            if (shown.Version < Models.MediaMetadata.CurrentVersion)
            {
                Assert.Equal("", shown.AltText);
                await Assert.ThrowsAnyAsync<Exception>(() => MetadataReader.ReadAsync(path, forWriting: true));
            }
        }
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
    public void OnlyJpegAndTiff_HoldTheFields()
    {
        Assert.True(XmpFields.CanWrite(@"C:\a.JPG"));
        Assert.True(XmpFields.CanWrite(@"C:\a.tiff"));
        Assert.False(XmpFields.CanWrite(@"C:\a.png"));
        Assert.False(XmpFields.CanWrite(@"C:\a.heic"));
        Assert.False(XmpFields.CanWrite(@"C:\a.mp4"));
    }

    private static Models.MediaItem ItemFor(string path)
    {
        var info = new FileInfo(path);
        return new Models.MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime);
    }
}
