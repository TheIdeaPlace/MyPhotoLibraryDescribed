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
    }

    [Fact]
    public void Replaced_ListsWhatsAlreadyThere_ButNotEmptyOrMatchingFields()
    {
        Assert.Empty(FileFields.Replaced("", "", "A dog."));
        Assert.Empty(FileFields.Replaced("A dog.", "A dog.", "A dog."));
        Assert.Equal([("caption", "Mine")], FileFields.Replaced("Mine", "", "A dog."));
        Assert.Equal([("caption", "Mine"), ("alt text", "Other app")], FileFields.Replaced("Mine", "Other app", "A dog."));
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
    public async Task AJpeg_GetsTheFields_Losslessly_KeepingItsDateTitleAndRating()
    {
        var path = _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { Title = "My title", Rating = 4, DateTaken = new DateTime(2022, 6, 1, 10, 0, 0) });
        var before = await MetadataReader.ReadAsync(path);
        var scan = Scan(File.ReadAllBytes(path));
        var modified = File.GetLastWriteTimeUtc(path);

        await XmpFields.WriteAsync(path, "A dog on a beach.", "A dog.", "");
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(new XmpFields.Fields("A dog on a beach.", "A dog.", ""), fields);
        Assert.True(scan.AsSpan().SequenceEqual(Scan(File.ReadAllBytes(path))), "The picture changed.");
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var after = await MetadataReader.ReadAsync(path);
        Assert.Equal(before.DateTaken, after.DateTaken);
        Assert.Equal(before.Rating, after.Rating);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
        // The standard caption is read first, ahead of the Title.
        Assert.Equal("A dog on a beach.", after.Caption);
        Assert.Equal("A dog.", after.AltText);

        // Written again, and cleared.
        await XmpFields.WriteAsync(path, "", null, null);
        Assert.Equal("", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("My title", (await MetadataReader.ReadAsync(path)).Caption);
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
