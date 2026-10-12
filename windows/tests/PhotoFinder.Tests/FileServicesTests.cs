using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Tests;

public class LibraryScannerTests
{
    [Fact]
    public void Scan_FindsPhotosAndVideosInSubfolders_AndNothingElse()
    {
        using var lib = new TestLibrary();
        lib.AddJpeg("a.jpg");
        lib.AddJpeg(@"Trip\Day 1\b.jpg");
        lib.AddFile("clip.mp4", "fake video");
        lib.AddFile("notes.txt");
        lib.AddFile(@"Trip\b.xmp");
        var items = LibraryScanner.Scan([lib.Root]);
        Assert.Equal(["a.jpg", "b.jpg", "clip.mp4"], items.Select(i => i.Name).Order());
        Assert.Contains(items, i => i.IsVideo);
    }

    [Fact]
    public void Scan_SkipsHiddenFiles()
    {
        using var lib = new TestLibrary();
        lib.AddJpeg("shown.jpg");
        var hidden = lib.AddJpeg("hidden.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Assert.Equal("shown.jpg", Assert.Single(LibraryScanner.Scan([lib.Root])).Name);
    }

    [Fact]
    public void Scan_ListsAFileOnce_WhenRootsOverlap_AndIgnoresMissingRoots()
    {
        using var lib = new TestLibrary();
        lib.AddJpeg(@"Sub\a.jpg");
        var items = LibraryScanner.Scan([lib.Root, lib.PathOf("Sub"), lib.PathOf("missing")]);
        Assert.Single(items);
    }

    [Fact]
    public void Scan_ReadsSizeAndDates()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg");
        var modified = new DateTime(2019, 6, 1, 8, 30, 0);
        File.SetLastWriteTime(path, modified);
        var item = Assert.Single(LibraryScanner.Scan([lib.Root]));
        Assert.Equal(new FileInfo(path).Length, item.Size);
        Assert.Equal(modified, item.Modified);
        Assert.False(item.IsOnlineOnly);
    }

    [Fact]
    public void Scan_StopsWhenCancelled()
    {
        using var lib = new TestLibrary();
        lib.AddJpeg("a.jpg");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => LibraryScanner.Scan([lib.Root], cancel.Token));
    }

    [Fact]
    public void NormalizeRoots_DropsDuplicatesAndFoldersInsideOthers()
    {
        var roots = LibraryScanner.NormalizeRoots([@"C:\Pictures", @"C:\Pictures\", @"c:\pictures\Camera Roll", @"C:\Pictures2", ""]);
        Assert.Equal([@"C:\Pictures", @"C:\Pictures2"], roots);
    }

    [Fact]
    public void IsInside_NeedsAWholeFolderName()
    {
        Assert.True(LibraryScanner.IsInside(@"C:\Pictures\a", @"C:\Pictures"));
        Assert.True(LibraryScanner.IsInside(@"C:\Pictures\a", @"C:\Pictures\"));
        Assert.False(LibraryScanner.IsInside(@"C:\Pictures2\a", @"C:\Pictures"));
        Assert.False(LibraryScanner.IsInside(@"C:\Pictures", @"C:\Pictures"));
    }
}

public class LibraryRootsTests
{
    [Fact]
    public void Combine_AddsExtraFoldersAndLeavesOutRemovedOnes()
    {
        var settings = new Settings { ExtraFolders = [@"D:\Photos"], RemovedFolders = [@"C:\Users\me\OneDrive\Pictures\"] };
        var roots = LibraryRoots.Combine([@"C:\Users\me\Pictures", @"C:\Users\me\OneDrive\Pictures"], settings);
        Assert.Equal([@"C:\Users\me\Pictures", @"D:\Photos"], roots);
    }

    [Fact]
    public async Task PicturesLibrary_ReturnsAtLeastOneFolder()
    {
        var folders = await LibraryRoots.PicturesLibraryAsync();
        Assert.NotEmpty(folders);
        Assert.All(folders, f => Assert.True(Path.IsPathRooted(f)));
    }
}

public class MetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_ReadsTitleKeywordsRatingAndDateTaken_FromARealJpeg()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions
        {
            Width = 80, Height = 60, Title = "Sunset at the pier", Keywords = ["beach", "sky"], Rating = 4,
            DateTaken = new DateTime(2018, 7, 4, 20, 15, 0),
        });
        var m = await MetadataReader.ReadAsync(path);
        Assert.Equal("Sunset at the pier", m.Caption);
        Assert.Equal("beach, sky", m.Keywords);
        Assert.Equal(4, m.Rating);
        Assert.Equal(new DateTime(2018, 7, 4, 20, 15, 0), m.DateTaken);
        Assert.Equal(80, m.Width);
        Assert.Equal(60, m.Height);
    }

    [Fact]
    public async Task ReadAsync_GivesEmptyValues_ForAJpegWithNoMetadata()
    {
        using var lib = new TestLibrary();
        var m = await MetadataReader.ReadAsync(lib.AddJpeg("plain.jpg"));
        Assert.Equal("", m.Caption);
        Assert.Equal("", m.Keywords);
        Assert.Equal(0, m.Rating);
        Assert.Null(m.DateTaken);
        Assert.Null(m.Latitude);
    }

    [Fact]
    public async Task ReadAsync_ThrowsForAMissingFile()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => MetadataReader.ReadAsync(@"C:\definitely\not\here.jpg"));
    }

    [Fact]
    public async Task ReadDetailsAsync_ReadsTheCamera()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { CameraMake = "Canon", CameraModel = "EOS R6" });
        var details = await MetadataReader.ReadDetailsAsync(path);
        Assert.Contains(new InfoField("Camera", "Canon EOS R6"), details);
    }

    [Fact]
    public void FromProperties_SwapsWidthAndHeight_ForSidewaysPhotos()
    {
        var m = MetadataReader.FromProperties(new Dictionary<string, object?>
        {
            ["System.Image.HorizontalSize"] = 4032u, ["System.Image.VerticalSize"] = 3024u, ["System.Photo.Orientation"] = (ushort)6,
        });
        Assert.Equal((3024, 4032), (m.Width, m.Height));
    }

    [Fact]
    public void FromProperties_ReadsVideoSizeDurationAndEncodedDate()
    {
        var encoded = new DateTimeOffset(2022, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var m = MetadataReader.FromProperties(new Dictionary<string, object?>
        {
            ["System.Video.FrameWidth"] = 1920u, ["System.Video.FrameHeight"] = 1080u,
            ["System.Media.Duration"] = 905_000_000UL, ["System.Media.DateEncoded"] = encoded,
        });
        Assert.Equal((1920, 1080), (m.Width, m.Height));
        Assert.Equal(90.5, m.DurationSeconds);
        Assert.Equal(encoded.LocalDateTime, m.DateTaken);
    }

    [Fact]
    public void FromProperties_TurnsGpsDegreesMinutesSecondsIntoSignedDecimals()
    {
        var m = MetadataReader.FromProperties(new Dictionary<string, object?>
        {
            ["System.GPS.Latitude"] = new[] { 47.0, 36.0, 0.0 }, ["System.GPS.LatitudeRef"] = "N",
            ["System.GPS.Longitude"] = new[] { 122.0, 19.0, 48.0 }, ["System.GPS.LongitudeRef"] = "W",
        });
        Assert.Equal(47.6, m.Latitude!.Value, 6);
        Assert.Equal(-122.33, m.Longitude!.Value, 6);
    }

    [Fact]
    public void FromProperties_IgnoresTheZeroDateWindowsUsesForNone()
    {
        var m = MetadataReader.FromProperties(new Dictionary<string, object?>
        {
            ["System.Photo.DateTaken"] = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        Assert.Null(m.DateTaken);
    }

    [Fact]
    public void DetailFields_FormatExposureLikeACamera()
    {
        var fields = MetadataReader.DetailFields(new Dictionary<string, object?>
        {
            ["System.Photo.CameraManufacturer"] = "Apple", ["System.Photo.CameraModel"] = "Apple iPhone 15",
            ["System.Photo.FNumber"] = 1.8, ["System.Photo.ExposureTime"] = 0.008, ["System.Photo.ISOSpeed"] = (ushort)64,
            ["System.Photo.FocalLength"] = 6.86, ["System.Photo.Flash"] = (byte)16,
        });
        // A model that already starts with the make isn't repeated.
        Assert.Contains(new InfoField("Camera", "Apple iPhone 15"), fields);
        Assert.Contains(new InfoField("Aperture", "f/1.8"), fields);
        Assert.Contains(new InfoField("Shutter Speed", "1/125 second"), fields);
        Assert.Contains(new InfoField("ISO", "64"), fields);
        Assert.Contains(new InfoField("Focal Length", "6.9 mm"), fields);
        Assert.Contains(new InfoField("Flash", "Did not fire"), fields);
    }

    [Theory]
    [InlineData(0.5, "1/2 second")]
    [InlineData(1.0, "1 second")]
    [InlineData(2.5, "2.5 seconds")]
    public void ExposureText(double seconds, string expected) => Assert.Equal(expected, MetadataReader.ExposureText(seconds));
}

public class MetadataWriterTests
{
    private static MediaItem ItemFor(string path)
    {
        var info = new FileInfo(path);
        return new MediaItem(path, info.Length, info.CreationTime, info.LastWriteTime);
    }

    [Fact]
    public async Task SetCaption_WritesTheTitle_AndKeepsTheModifiedDate()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg");
        var modified = new DateTime(2015, 2, 3, 4, 5, 6);
        File.SetLastWriteTime(path, modified);

        var result = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "  A day at the zoo  ");

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal("A day at the zoo", (await MetadataReader.ReadAsync(path)).Caption);
        Assert.Equal(modified, File.GetLastWriteTime(path));
    }

    [Fact]
    public async Task SetCaption_Empty_RemovesIt()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { Title = "Old" });
        await MetadataWriter.SetCaptionAsync([ItemFor(path)], "   ");
        Assert.Equal("", (await MetadataReader.ReadAsync(path)).Caption);
    }

    [Fact]
    public async Task SetRating_WritesStars_AndZeroClearsThem()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("a.jpg");
        await MetadataWriter.SetRatingAsync([ItemFor(path)], 5);
        Assert.Equal(5, (await MetadataReader.ReadAsync(path)).Rating);
        await MetadataWriter.SetRatingAsync([ItemFor(path)], 2);
        Assert.Equal(2, (await MetadataReader.ReadAsync(path)).Rating);
        await MetadataWriter.SetRatingAsync([ItemFor(path)], 0);
        Assert.Equal(0, (await MetadataReader.ReadAsync(path)).Rating);
    }

    [Fact]
    public async Task AReadOnlyFile_IsReportedAndTheOthersStillChange()
    {
        using var lib = new TestLibrary();
        var good = lib.AddJpeg("good.jpg");
        var locked = lib.AddJpeg("locked.jpg");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        var result = await MetadataWriter.SetCaptionAsync([ItemFor(good), ItemFor(locked)], "Hello");

        Assert.Equal(1, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("locked.jpg", failure.Item.Name);
        Assert.Contains("locked.jpg", result.FailureSummary);
        Assert.Equal("Hello", (await MetadataReader.ReadAsync(good)).Caption);
    }

    [Fact]
    public async Task AFileThatIsntReallyAPicture_IsReported()
    {
        using var lib = new TestLibrary();
        var fake = lib.AddFile("fake.jpg");
        var result = await MetadataWriter.SetRatingAsync([ItemFor(fake)], 3);
        Assert.Equal(0, result.Succeeded);
        Assert.Single(result.Failures);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("Hello", "Hello", true)]
    [InlineData(" Hello ", "Hello", true)]
    [InlineData("Other", "Hello", false)]
    [InlineData(null, "Hello", false)]
    public void Matches_ComparesCaptions(string? stored, string? written, bool expected) =>
        Assert.Equal(expected, MetadataWriter.Matches(stored, written));

    [Fact]
    public void Matches_ComparesRatingsAsStars()
    {
        Assert.True(MetadataWriter.Matches(99u, 99u));
        Assert.True(MetadataWriter.Matches(95u, 99u));
        Assert.False(MetadataWriter.Matches(75u, 99u));
        Assert.False(MetadataWriter.Matches(null, 99u));
        Assert.True(MetadataWriter.Matches(0u, null));
    }

    [Fact]
    public void FailureSummary_CountsSeveralFailures()
    {
        var item = new MediaItem(@"C:\a.jpg", 1, DateTime.Now, DateTime.Now);
        var result = new WriteResult(0, [(item, "a.jpg is read-only or in use."), (item, "b")]);
        Assert.Equal("2 items couldn't be changed. a.jpg is read-only or in use.", result.FailureSummary);
        Assert.Equal("", new WriteResult(3, []).FailureSummary);
    }
}

public class MetadataCacheTests
{
    [Fact]
    public void DetailsCachedBeforeTheStandardCaptionWasRead_AreReadAgain_ForJpegsOnly()
    {
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}.json"));
        var date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jpeg = new MediaItem(@"C:\p.jpg", 100, date, date);
        var png = new MediaItem(@"C:\p.png", 100, date, date);
        cache.Remember(jpeg, new MediaMetadata { Caption = "Old" });
        cache.Remember(png, new MediaMetadata { Caption = "Old" });
        Assert.Null(cache.Lookup(jpeg));
        Assert.Equal("Old", cache.Lookup(png)!.Caption);
    }

    [Fact]
    public void Lookup_HitsOnlyWhileSizeAndDateMatch()
    {
        using var lib = new TestLibrary();
        var cache = new MetadataCache(lib.PathOf("cache.json"));
        var date = new DateTime(2020, 1, 1);
        var item = new MediaItem(@"C:\p\a.jpg", 100, date, date);
        cache.Remember(item, new MediaMetadata { Caption = "Hi", Version = MediaMetadata.CurrentVersion });
        Assert.Equal("Hi", cache.Lookup(item)!.Caption);
        Assert.Equal("Hi", cache.Lookup(new MediaItem(@"C:\P\A.JPG", 100, date, date))!.Caption);
        Assert.Null(cache.Lookup(new MediaItem(@"C:\p\a.jpg", 101, date, date)));
        Assert.Null(cache.Lookup(new MediaItem(@"C:\p\a.jpg", 100, date, date.AddSeconds(1))));
    }

    [Fact]
    public void DetailsFromBeforeCaptionsWereRead_AreReadAgain_ExceptForOnlineOnlyFiles()
    {
        using var lib = new TestLibrary();
        var cache = new MetadataCache(lib.PathOf("cache.json"));
        var date = new DateTime(2020, 1, 1);
        var local = new MediaItem(@"C:\p.jpg", 100, date, date);
        var online = new MediaItem(@"C:\p.jpg", 100, date, date) { IsOnlineOnly = true };
        cache.Remember(local, new MediaMetadata { Rating = 5 });
        cache.Remember(online, new MediaMetadata { Rating = 5 });
        Assert.Null(cache.Lookup(local));
        // Not read until it's downloaded: its older details (date taken, rating) are kept meanwhile.
        Assert.Equal(5, cache.Lookup(online)!.Rating);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var lib = new TestLibrary();
        var file = lib.PathOf(@"data\cache.json");
        var date = new DateTime(2020, 1, 1);
        var item = new MediaItem(@"C:\p\a.jpg", 100, date, date);
        var cache = new MetadataCache(file);
        cache.Remember(item, new MediaMetadata { Caption = "Hi", Rating = 3, Latitude = 1.5, DateTaken = date, Version = MediaMetadata.CurrentVersion });
        cache.Save();
        var loaded = new MetadataCache(file).Lookup(item);
        Assert.Equal(new MediaMetadata { Caption = "Hi", Rating = 3, Latitude = 1.5, DateTaken = date, Version = MediaMetadata.CurrentVersion }, loaded);
    }

    [Fact]
    public void ADamagedCacheFile_StartsEmpty()
    {
        using var lib = new TestLibrary();
        var file = lib.AddFile("cache.json", "{ this is not json");
        Assert.Equal(0, new MetadataCache(file).Count);
    }

    [Fact]
    public void KeepOnly_ForgetsFilesThatAreGone()
    {
        using var lib = new TestLibrary();
        var cache = new MetadataCache(lib.PathOf("cache.json"));
        var a = new MediaItem(@"C:\p\a.jpg", 1, DateTime.Now, DateTime.Now);
        var b = new MediaItem(@"C:\p\b.jpg", 1, DateTime.Now, DateTime.Now);
        cache.Remember(a, MediaMetadata.Empty);
        cache.Remember(b, MediaMetadata.Empty);
        cache.KeepOnly([a]);
        Assert.Equal(1, cache.Count);
        Assert.Null(cache.Lookup(b));
    }
}

public class FileActionsTests
{
    [Fact]
    public void ExportInto_NeverOverwrites_AndKeepsTheDates()
    {
        using var lib = new TestLibrary();
        var source = lib.AddJpeg(@"src\IMG_1.JPG");
        var modified = new DateTime(2012, 12, 12, 12, 12, 12);
        File.SetLastWriteTime(source, modified);
        var target = Directory.CreateDirectory(lib.PathOf("out")).FullName;

        var first = FileActions.ExportInto(source, target);
        var second = FileActions.ExportInto(source, target);
        var third = FileActions.ExportInto(source, target);

        Assert.Equal("IMG_1.JPG", Path.GetFileName(first));
        Assert.Equal("IMG_1 2.JPG", Path.GetFileName(second));
        Assert.Equal("IMG_1 3.JPG", Path.GetFileName(third));
        Assert.Equal(modified, File.GetLastWriteTime(second));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(third));
    }

    [Fact]
    public void ExportInto_AMissingFolder_Throws()
    {
        using var lib = new TestLibrary();
        var source = lib.AddJpeg("a.jpg");
        Assert.ThrowsAny<IOException>(() => FileActions.ExportInto(source, lib.PathOf("nope")));
    }

    [Fact]
    public void UniquePath_HandlesNamesWithoutExtensions()
    {
        using var lib = new TestLibrary();
        lib.AddFile("README");
        Assert.Equal(lib.PathOf("README 2"), FileActions.UniquePath(lib.Root, "README"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var lib = new TestLibrary();
        var path = lib.PathOf(@"data\Settings.json");
        var settings = new Settings { ViewMode = ViewMode.Gallery, SortColumn = "name", SortDescending = false, ExtraFolders = [@"D:\x"], DescriptionKind = "brief" };
        settings.Columns["keywords"] = true;
        settings.Save(path);
        var loaded = Settings.Load(path);
        Assert.Equal(ViewMode.Gallery, loaded.ViewMode);
        Assert.Equal("name", loaded.SortColumn);
        Assert.False(loaded.SortDescending);
        Assert.Equal([@"D:\x"], loaded.ExtraFolders);
        Assert.Equal("brief", loaded.DescriptionKind);
        Assert.True(loaded.IsColumnVisible(Columns.Find("keywords")!));
    }

    [Fact]
    public void MissingOrDamagedSettings_GiveTheDefaults()
    {
        using var lib = new TestLibrary();
        Assert.Equal(ViewMode.List, Settings.Load(lib.PathOf("none.json")).ViewMode);
        var damaged = lib.AddFile("bad.json", "[1,2");
        Assert.Equal("date", Settings.Load(damaged).SortColumn);
    }

    [Fact]
    public void ColumnVisibility_UsesDefaultsUntilChanged_AndNameIsAlwaysShown()
    {
        var settings = new Settings();
        Assert.True(settings.IsColumnVisible(Columns.Find("caption")!));
        Assert.False(settings.IsColumnVisible(Columns.Find("keywords")!));
        settings.Columns["caption"] = false;
        Assert.False(settings.IsColumnVisible(Columns.Find("caption")!));
        settings.Columns["name"] = false;
        Assert.True(settings.IsColumnVisible(Columns.NameColumn));
    }
}
