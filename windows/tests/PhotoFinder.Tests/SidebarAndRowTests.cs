using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Tests;

public class SidebarBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0);
    private const string Root = @"C:\Pictures";

    private static MediaItem Item(string relative, DateTime date, MediaMetadata? m = null, DateTime? created = null) =>
        new(Path.Combine(Root, relative), 10, created ?? date, date) { Metadata = m };

    private static List<MediaItem> Library() =>
    [
        Item(@"Camera Roll\IMG_1.jpg", new DateTime(2024, 3, 5), new MediaMetadata { Rating = 5 }),
        Item(@"Camera Roll\IMG_2.jpg", new DateTime(2024, 3, 9)),
        Item(@"Camera Roll\2023\IMG_0.jpg", new DateTime(2023, 12, 25)),
        Item(@"Screenshots\Screenshot 1.png", new DateTime(2024, 7, 1)),
        Item(@"clip.mp4", new DateTime(2024, 7, 2), created: Now.AddDays(-2)),
    ];

    [Fact]
    public void Build_HasLibraryDatesAndFoldersSections()
    {
        var sections = SidebarBuilder.Build(Library(), [Root], Now);
        Assert.Equal(["Library", "Dates", "Folders"], sections.Select(s => s.Title));
        Assert.All(sections, s => Assert.Equal(SourceKind.Section, s.Kind));
    }

    [Fact]
    public void Library_ListsAllPhotosAndOnlyNonEmptySmartAlbums()
    {
        var library = SidebarBuilder.Build(Library(), [Root], Now)[0];
        var titles = library.Children.Select(c => $"{c.Title} {c.Count}").ToList();
        Assert.Equal(["All Photos 5", "Favorites 1", "Recently Added 1", "Videos 1", "Screenshots 1"], titles);
        Assert.All(library.Children, c => Assert.Equal(SidebarIds.LibrarySection, c.ParentId));
    }

    [Fact]
    public void AllPhotos_IsThereEvenWhenTheLibraryIsEmpty()
    {
        var sections = SidebarBuilder.Build([], [Root], Now);
        var library = Assert.Single(sections);
        var all = Assert.Single(library.Children);
        Assert.Equal(SidebarIds.AllPhotos, all.Id);
        Assert.Equal(0, all.Count);
    }

    [Fact]
    public void Dates_AreYearsNewestFirst_WithMonthsInside()
    {
        var dates = SidebarBuilder.Build(Library(), [Root], Now)[1];
        Assert.Equal(["2024", "2023"], dates.Children.Select(y => y.Title));
        var year2024 = dates.Children[0];
        Assert.Equal(4, year2024.Count);
        Assert.Equal([7, 3], year2024.Children.Select(m => m.Month));
        Assert.Equal(2, year2024.Children[1].Count);
        Assert.Equal(year2024.Id, year2024.Children[0].ParentId);
        Assert.Equal(SidebarIds.DatesSection, year2024.ParentId);
    }

    [Fact]
    public void Folders_AreATreeWithCountsIncludingSubfolders()
    {
        var folders = SidebarBuilder.Build(Library(), [Root], Now)[2];
        var root = Assert.Single(folders.Children);
        Assert.Equal("Pictures", root.Title);
        Assert.Equal(5, root.Count);
        Assert.Equal(["Camera Roll", "Screenshots"], root.Children.Select(c => c.Title));
        var cameraRoll = root.Children[0];
        Assert.Equal(3, cameraRoll.Count);
        Assert.Equal("2023", Assert.Single(cameraRoll.Children).Title);
        Assert.Equal(root.Id, cameraRoll.ParentId);
    }

    [Fact]
    public void RootsWithTheSameName_AreShownByTheirPaths()
    {
        var items = new List<MediaItem>
        {
            new(@"C:\Pictures\a.jpg", 1, Now, Now),
            new(@"D:\Pictures\b.jpg", 1, Now, Now),
        };
        var folders = SidebarBuilder.Build(items, [@"C:\Pictures", @"D:\Pictures"], Now)[2];
        Assert.Equal([@"C:\Pictures", @"D:\Pictures"], folders.Children.Select(c => c.Title));
    }

    [Fact]
    public void Matches_RecentlyAddedUsesTheWindowAndIgnoresFutureDates()
    {
        var recent = new SidebarItem { Id = "r", Title = "r", Kind = SourceKind.RecentlyAdded, ContainerKind = "Album" };
        Assert.True(SidebarBuilder.Matches(recent, Item("a.jpg", Now.AddDays(-29)), Now));
        Assert.False(SidebarBuilder.Matches(recent, Item("a.jpg", Now.AddDays(-31)), Now));
        Assert.False(SidebarBuilder.Matches(recent, Item("a.jpg", Now.AddYears(5)), Now));
    }

    [Fact]
    public void SameAs_NoticesCountAndChildChanges()
    {
        var a = SidebarBuilder.Build(Library(), [Root], Now);
        var b = SidebarBuilder.Build(Library(), [Root], Now);
        Assert.True(a[0].SameAs(b[0]));
        var more = Library();
        more.Add(Item("new.jpg", new DateTime(2024, 3, 1)));
        var c = SidebarBuilder.Build(more, [Root], Now);
        Assert.False(a[0].SameAs(c[0]));
    }

    [Fact]
    public void Index_FindsEveryItemById()
    {
        var index = SidebarBuilder.Index(SidebarBuilder.Build(Library(), [Root], Now));
        Assert.Contains(SidebarIds.AllPhotos, index.Keys);
        Assert.Contains(SidebarIds.Month(2024, 3), index.Keys);
        Assert.Contains(SidebarIds.Folder(@"C:\Pictures\Camera Roll\2023"), index.Keys);
    }
}

public class RowQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 11);

    private static SidebarItem Folder(string path, params SidebarItem[] children) => new()
    {
        Id = SidebarIds.Folder(path), Title = Path.GetFileName(path), Kind = SourceKind.Folder, FolderPath = path,
        Children = children, ContainerKind = "Folder", Count = 1,
    };

    [Fact]
    public void AFolderLists_ItsSubfoldersThenThePhotosDirectlyInIt()
    {
        var sub = Folder(@"C:\P\Trip");
        var folder = Folder(@"C:\P", sub);
        var items = new List<MediaItem>
        {
            new(@"C:\P\a.jpg", 1, Now, Now),
            new(@"C:\P\Trip\b.jpg", 1, Now, Now),
        };
        var rows = RowQuery.RowsFor(folder, items, Now);
        Assert.Equal(["Trip", "a.jpg"], rows.Select(r => r.Name));
        Assert.True(rows[0].IsContainer);
    }

    [Fact]
    public void AYearLists_OnlyItsMonths()
    {
        var month = new SidebarItem { Id = "m", Title = "March 2024", Kind = SourceKind.Month, Year = 2024, Month = 3, ContainerKind = "Month" };
        var year = new SidebarItem { Id = "y", Title = "2024", Kind = SourceKind.Year, Year = 2024, Children = [month], ContainerKind = "Year" };
        var items = new List<MediaItem> { new(@"C:\P\a.jpg", 1, new DateTime(2024, 3, 2), new DateTime(2024, 3, 2)) };
        var rows = RowQuery.RowsFor(year, items, Now);
        Assert.Equal("March 2024", Assert.Single(rows).Name);
    }

    [Fact]
    public void Search_MatchesNameKindCaptionKeywordsAndDate_IgnoringCase()
    {
        var row = FileRow.ForItem(new MediaItem(@"C:\P\IMG_9.jpg", 1, new DateTime(2024, 3, 2), new DateTime(2024, 3, 2))
        {
            Metadata = new MediaMetadata { Caption = "Birthday cake", Keywords = "family" },
        });
        Assert.True(RowQuery.MatchesSearch(row, "img_9"));
        Assert.True(RowQuery.MatchesSearch(row, "photo"));
        Assert.True(RowQuery.MatchesSearch(row, "CAKE"));
        Assert.True(RowQuery.MatchesSearch(row, "family"));
        Assert.True(RowQuery.MatchesSearch(row, "2024"));
        Assert.True(RowQuery.MatchesSearch(row, "   "));
        Assert.False(RowQuery.MatchesSearch(row, "zebra"));
    }

    private static FileRow Photo(string name, DateTime date, long size = 1) =>
        FileRow.ForItem(new MediaItem($@"C:\P\{name}", size, date, date));

    [Fact]
    public void Sorting_PutsFoldersFirst_InEitherDirection()
    {
        var rows = new List<FileRow>
        {
            Photo("b.jpg", new DateTime(2024, 1, 1)),
            FileRow.ForContainer(Folder(@"C:\P\Zoo")),
            Photo("a.jpg", new DateTime(2023, 1, 1)),
        };
        Assert.Equal(["Zoo", "a.jpg", "b.jpg"], rows.Order(RowQuery.Comparer("name", false)).Select(r => r.Name));
        Assert.Equal(["Zoo", "b.jpg", "a.jpg"], rows.Order(RowQuery.Comparer("name", true)).Select(r => r.Name));
        Assert.Equal(["Zoo", "b.jpg", "a.jpg"], rows.Order(RowQuery.Comparer("date", true)).Select(r => r.Name));
    }

    [Fact]
    public void FoldersWithoutDates_StayAToZ_WhenNewestFirst()
    {
        var rows = new[] { FileRow.ForContainer(Folder(@"C:\P\Apple")), FileRow.ForContainer(Folder(@"C:\P\Zoo")) };
        Assert.Equal(["Apple", "Zoo"], rows.Order(RowQuery.Comparer("date", true)).Select(r => r.Name));
    }

    [Fact]
    public void Names_SortNaturally()
    {
        var rows = new[] { Photo("IMG_10.jpg", Now), Photo("IMG_2.jpg", Now), Photo("IMG_1.jpg", Now) };
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg"], rows.Order(RowQuery.Comparer("name", false)).Select(r => r.Name));
    }

    [Fact]
    public void Ties_FallBackToName()
    {
        var rows = new[] { Photo("c.jpg", Now, 5), Photo("a.jpg", Now, 5), Photo("b.jpg", Now, 9) };
        Assert.Equal(["a.jpg", "c.jpg", "b.jpg"], rows.Order(RowQuery.Comparer("size", false)).Select(r => r.Name));
    }
}
