using PhotoFinder.Models;

namespace PhotoFinder.Tests;

public class MediaTypesTests
{
    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.heic", true)]
    [InlineData("a.dng", true)]
    [InlineData("a.mov", true)]
    [InlineData("a.MP4", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.xmp", false)]
    [InlineData("noextension", false)]
    public void IsMedia_KnowsPhotoVideoAndRawExtensions(string name, bool expected) =>
        Assert.Equal(expected, MediaTypes.IsMedia(name));

    [Fact]
    public void KindOf_TellsVideosFromImages()
    {
        Assert.Equal(MediaKind.Video, MediaTypes.KindOf(@"C:\x\clip.MOV"));
        Assert.Equal(MediaKind.Image, MediaTypes.KindOf(@"C:\x\photo.cr3"));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(12u, 1)]
    [InlineData(13u, 2)]
    [InlineData(25u, 2)]
    [InlineData(50u, 3)]
    [InlineData(75u, 4)]
    [InlineData(87u, 4)]
    [InlineData(88u, 5)]
    [InlineData(99u, 5)]
    public void StarsFromRating_ReadsWindowsRatingsInRanges(uint value, int stars) =>
        Assert.Equal(stars, MediaTypes.StarsFromRating(value));

    [Fact]
    public void RatingFromStars_RoundTripsEveryStar()
    {
        for (var stars = 0; stars <= 5; stars++)
            Assert.Equal(stars, MediaTypes.StarsFromRating(MediaTypes.RatingFromStars(stars)));
        Assert.Equal(0u, MediaTypes.RatingFromStars(-3));
        Assert.Equal(99u, MediaTypes.RatingFromStars(9));
    }

    private static MediaItem Item(string path, MediaMetadata? metadata = null) =>
        new(path, 100, new DateTime(2024, 1, 1), new DateTime(2024, 1, 1)) { Metadata = metadata };

    [Fact]
    public void Screenshots_AreFoundByFolderOrName()
    {
        Assert.True(MediaTypes.IsScreenshot(Item(@"C:\Pictures\Screenshots\anything.png")));
        Assert.True(MediaTypes.IsScreenshot(Item(@"C:\Pictures\Screenshot 2024-01-01 101010.png")));
        Assert.False(MediaTypes.IsScreenshot(Item(@"C:\Pictures\Camera Roll\IMG_1.jpg")));
        Assert.False(MediaTypes.IsScreenshot(Item(@"C:\Pictures\Screenshots\recording.mp4")));
    }

    [Fact]
    public void ScreenRecordings_AreVideosInCapturesOrNamedSo()
    {
        Assert.True(MediaTypes.IsScreenRecording(Item(@"C:\Videos\Captures\game.mp4")));
        Assert.True(MediaTypes.IsScreenRecording(Item(@"C:\Videos\Screen Recording 2024.mp4")));
        Assert.False(MediaTypes.IsScreenRecording(Item(@"C:\Videos\Captures\still.png")));
    }

    [Fact]
    public void KindDescription_NamesEachKind()
    {
        Assert.Equal("Photo", MediaTypes.KindDescription(Item(@"C:\p\IMG.jpg")));
        Assert.Equal("Video", MediaTypes.KindDescription(Item(@"C:\p\clip.mov")));
        Assert.Equal("Screenshot", MediaTypes.KindDescription(Item(@"C:\p\Screenshots\a.png")));
        Assert.Equal("RAW Photo", MediaTypes.KindDescription(Item(@"C:\p\a.nef")));
        Assert.Equal("Animated Image", MediaTypes.KindDescription(Item(@"C:\p\a.gif")));
        Assert.Equal("Panorama", MediaTypes.KindDescription(Item(@"C:\p\a.jpg", new MediaMetadata { Width = 9000, Height = 3000 })));
        Assert.Equal("Photo", MediaTypes.KindDescription(Item(@"C:\p\a.jpg", new MediaMetadata { Width = 4000, Height = 3000 })));
    }

    [Fact]
    public void FileTypeDescription_UsesWindowsNamesAndCachesByExtension()
    {
        var jpeg = MediaTypes.FileTypeDescription(@"C:\a\b.jpg");
        Assert.False(string.IsNullOrWhiteSpace(jpeg));
        Assert.Equal(jpeg, MediaTypes.FileTypeDescription(@"D:\other\c.JPG"));
        Assert.Equal("", MediaTypes.FileTypeDescription(@"C:\a\noext"));
    }
}

public class MediaItemTests
{
    [Fact]
    public void Date_PrefersDateTaken_ThenTheEarlierFileDate()
    {
        var created = new DateTime(2024, 5, 1);
        var modified = new DateTime(2020, 3, 2);
        var item = new MediaItem(@"C:\p\a.jpg", 1, created, modified);
        // A copied file has a new creation date but keeps its modified date.
        Assert.Equal(modified, item.Date);
        item.Metadata = new MediaMetadata { DateTaken = new DateTime(2019, 7, 4) };
        Assert.Equal(new DateTime(2019, 7, 4), item.Date);
    }

    [Fact]
    public void IsFavorite_MeansFiveStars()
    {
        var item = new MediaItem(@"C:\p\a.jpg", 1, DateTime.Now, DateTime.Now);
        Assert.False(item.IsFavorite);
        item.Metadata = new MediaMetadata { Rating = 4 };
        Assert.False(item.IsFavorite);
        item.Metadata = new MediaMetadata { Rating = 5 };
        Assert.True(item.IsFavorite);
    }
}

[Collection(SharedState.Name)]
public class FileRowTests
{
    private static FileRow Row(MediaMetadata? m = null, string path = @"C:\Pictures\IMG_1234.JPG") =>
        FileRow.ForItem(new MediaItem(path, 2_500_000, new DateTime(2024, 3, 3, 16, 5, 0), new DateTime(2024, 3, 3, 16, 5, 0)) { Metadata = m });

    [Fact]
    public void PhotoRow_FormatsItsColumns()
    {
        var row = Row(new MediaMetadata { Width = 4032, Height = 3024, Caption = "Sunset", Keywords = "beach, sky", Rating = 3, Latitude = 47.6, Longitude = -122.3 });
        Assert.Equal("IMG_1234.JPG", row.Name);
        Assert.Equal("4032 × 3024", row.Dimensions);
        Assert.Equal("Landscape", row.Orientation);
        Assert.Equal("12.2 MP", row.MegapixelsText);
        Assert.Equal("3 stars", row.RatingText);
        Assert.Equal("Sunset", row.Caption);
        Assert.Equal("47.6000, -122.3000", row.LocationText);
        Assert.Equal("2.4 MB", row.SizeText);
        Assert.Equal("", row.DurationText);
        Assert.False(row.IsContainer);
    }

    [Fact]
    public void RowWithoutDetails_StillHasNameDateAndKind()
    {
        var row = Row();
        Assert.Equal("IMG_1234.JPG", row.Name);
        Assert.Equal("Photo", row.Kind);
        Assert.NotEqual("", row.DateText);
        Assert.Equal("", row.Dimensions);
        Assert.Equal("", row.Orientation);
    }

    [Fact]
    public void VideoRow_ShowsDuration()
    {
        var row = Row(new MediaMetadata { DurationSeconds = 75.4, Width = 1920, Height = 1080 }, @"C:\v\clip.mp4");
        Assert.Equal("1:15", row.DurationText);
        Assert.Equal("Video", row.Kind);
        Assert.Equal(75.4, row.Duration);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.6, "1:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7322, "2:02:02")]
    public void FormatDuration_UsesHoursOnlyWhenNeeded(double seconds, string expected) =>
        Assert.Equal(expected, FileRow.FormatDuration(seconds));

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    public void FormatSize_ReadsLikeFileExplorer(long bytes, string expected) =>
        Assert.Equal(expected, FileRow.FormatSize(bytes));

    [Fact]
    public void AccessibleName_FollowsTheVisibleColumns()
    {
        var previous = FileRow.SpokenColumns;
        try
        {
            var row = Row(new MediaMetadata { Caption = "Sunset", Rating = 5, Width = 10, Height = 10 });
            FileRow.SpokenColumns = ["kind", "caption", "favorite"];
            Assert.Equal("IMG_1234.JPG, Photo, Caption: Sunset, Favorite", row.AccessibleName);
            FileRow.SpokenColumns = ["dimensions"];
            Assert.Equal("IMG_1234.JPG, 10 × 10", row.AccessibleName);
            // Empty columns aren't spoken at all, rather than as blanks.
            FileRow.SpokenColumns = ["keywords", "duration"];
            Assert.Equal("IMG_1234.JPG", row.AccessibleName);
        }
        finally
        {
            FileRow.SpokenColumns = previous;
        }
    }

    [Fact]
    public void SpokenDescription_IsNameKindDateAndCaption()
    {
        var row = Row(new MediaMetadata { Caption = "Dog on beach" });
        Assert.StartsWith("IMG_1234.JPG, Photo, ", row.SpokenDescription);
        Assert.EndsWith(", Caption: Dog on beach", row.SpokenDescription);
    }

    [Fact]
    public void ContainerRow_DescribesWhatsInside()
    {
        var month = new SidebarItem { Id = "month:2024-3", Title = "March 2024", Kind = SourceKind.Month, Year = 2024, Month = 3, Count = 1, ContainerKind = "Month" };
        var row = FileRow.ForContainer(month);
        Assert.True(row.IsContainer);
        Assert.Equal("Month, 1 item", row.Kind);
        Assert.Equal(new DateTime(2024, 3, 1), row.Date);
        Assert.Equal("Month", row.FileType);
    }

    [Fact]
    public void Refresh_PicksUpNewDetailsAndTellsTheList()
    {
        var item = new MediaItem(@"C:\p\a.jpg", 1, DateTime.Now, DateTime.Now);
        var row = FileRow.ForItem(item);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.Metadata = new MediaMetadata { Caption = "New" };
        row.Refresh();
        Assert.Equal("New", row.Caption);
        Assert.Contains(string.Empty, changed);
    }
}
