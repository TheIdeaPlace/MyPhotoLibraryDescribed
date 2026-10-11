using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Tests;

/// <summary>The view model against a real folder of real JPEGs: scanning, reading details, navigating,
/// searching, sorting, and writing favorites and captions back into the files.</summary>
[Collection(SharedState.Name)]
public sealed class LibraryModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];
    private readonly List<(IReadOnlyList<string> Ids, bool Focus)> _selections = [];
    private readonly List<string> _errors = [];

    public LibraryModelTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg(@"Camera Roll\IMG_1.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 3, 5, 9, 0, 0), Title = "Breakfast" });
        _lib.AddJpeg(@"Camera Roll\IMG_2.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 3, 9, 9, 0, 0), Rating = 5 });
        _lib.AddJpeg(@"Camera Roll\IMG_10.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2023, 12, 25, 9, 0, 0), Keywords = ["holiday"] });
        _lib.AddJpeg(@"Screenshots\Screenshot 2024-07-01.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 7, 1) });
        _lib.AddFile("notes.txt");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private async Task<LibraryModel> StartAsync(Settings? settings = null)
    {
        var model = new LibraryModel(settings ?? new Settings(), _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\Metadata.json")), () => Task.FromResult(new List<string> { _lib.Root }),
            // Years after the files were made, so nothing counts as Recently Added.
            () => DateTime.Now.AddYears(1));
        model.SelectionRequested += (ids, focus) =>
        {
            _selections.Add((ids, focus));
            model.SelectedRows = model.Rows.Where(r => ids.Contains(r.Id)).ToList();
        };
        model.ErrorRaised += _errors.Add;
        await model.StartAsync();
        await model.DetailFillTask;
        return model;
    }

    private static void Select(LibraryModel model, params string[] names) =>
        model.SelectedRows = model.Rows.Where(r => names.Contains(r.Name)).ToList();

    [WpfFact]
    public async Task Start_ShowsAllPhotos_NewestFirst_WithDetailsRead()
    {
        using var model = await StartAsync();
        Assert.Equal(SidebarIds.AllPhotos, model.CurrentSource?.Id);
        Assert.Equal(["Screenshot 2024-07-01.jpg", "IMG_2.jpg", "IMG_1.jpg", "IMG_10.jpg"], model.Rows.Select(r => r.Name));
        Assert.Equal("Breakfast", model.Rows.Single(r => r.Name == "IMG_1.jpg").Caption);
        Assert.Equal("4 items", model.StatusText);
        Assert.Null(model.DetailProgress);
        Assert.Empty(_errors);
    }

    [WpfFact]
    public async Task Sidebar_HasSmartAlbumsDatesAndFolders_FromTheDetails()
    {
        using var model = await StartAsync();
        Assert.Equal(["Library", "Dates", "Folders"], model.Sections.Select(s => s.Title));
        var library = model.Sections[0].Children.Select(c => $"{c.Title} {c.Count}");
        Assert.Equal(["All Photos 4", "Favorites 1", "Screenshots 1"], library);
        // Dates come from Date Taken, not the files' own dates, which are all today.
        Assert.Equal(["2024", "2023"], model.Sections[1].Children.Select(c => c.Title));
        Assert.True(model.Sections.All(s => s.IsExpanded));
    }

    [WpfFact]
    public async Task Navigate_IntoAFolderAndBackUp_SelectsWhereYouCameFrom()
    {
        using var model = await StartAsync();
        var root = model.Sections[2].Children[0];
        model.Navigate(root.Id);
        Assert.Equal(["Camera Roll", "Screenshots"], model.Rows.Select(r => r.Name));
        Assert.Contains($"{root.Title}, 2 items", _announced);

        Select(model, "Camera Roll");
        model.Open();
        Assert.Equal("Camera Roll", model.Title);
        Assert.Equal(["IMG_2.jpg", "IMG_1.jpg", "IMG_10.jpg"], model.Rows.Select(r => r.Name));

        model.GoToEnclosingFolder();
        Assert.Equal(root.Id, model.CurrentSource?.Id);
        var (ids, focus) = _selections[^1];
        Assert.Equal(SidebarIds.Folder(_lib.PathOf("Camera Roll")), Assert.Single(ids));
        Assert.True(focus);
    }

    [WpfFact]
    public async Task GoingUpFromAYear_ReachesTheDatesSection_AndFromThereNothing()
    {
        using var model = await StartAsync();
        model.Navigate(SidebarIds.Year(2024));
        Assert.Equal(["July 2024", "March 2024"], model.Rows.Select(r => r.Name));
        model.GoToEnclosingFolder();
        Assert.Equal(SidebarIds.DatesSection, model.CurrentSource?.Id);
        model.GoToEnclosingFolder();
        Assert.Equal(SidebarIds.DatesSection, model.CurrentSource?.Id);
    }

    [WpfFact]
    public async Task Search_FiltersByCaptionKeywordsAndName_AndClearsOnNavigation()
    {
        using var model = await StartAsync();
        model.SearchText = "holiday";
        Assert.Equal("IMG_10.jpg", Assert.Single(model.Rows).Name);
        model.SearchText = "breakfast";
        Assert.Equal("IMG_1.jpg", Assert.Single(model.Rows).Name);
        model.SearchText = "nothing matches this";
        Assert.Empty(model.Rows);
        Assert.Equal("0 items", model.StatusText);
        model.Navigate(SidebarIds.AllPhotos);
        Assert.Equal("", model.SearchText);
        Assert.Equal(4, model.Rows.Count);
    }

    [WpfFact]
    public async Task Search_KeepsTheSelectionWhenItsStillShowing()
    {
        using var model = await StartAsync();
        Select(model, "IMG_1.jpg");
        model.SearchText = "IMG";
        Assert.Equal("IMG_1.jpg", Assert.Single(model.SelectedRows).Name);
    }

    [WpfFact]
    public async Task SortBy_TogglesDirection_AnnouncesIt_AndIsRemembered()
    {
        var settings = new Settings();
        using var model = await StartAsync(settings);
        model.SortBy("name");
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg", "Screenshot 2024-07-01.jpg"], model.Rows.Select(r => r.Name));
        Assert.Contains("Sorted by Name, ascending", _announced);
        model.SortBy("name");
        Assert.Equal("Screenshot 2024-07-01.jpg", model.Rows[0].Name);
        Assert.Equal("name", Settings.Load(_lib.PathOf(@"data\Settings.json")).SortColumn);
    }

    [WpfFact]
    public async Task ToggleFavorite_WritesFiveStars_AndFavoritesFollows()
    {
        using var model = await StartAsync();
        Select(model, "IMG_1.jpg");
        await model.ToggleFavoriteAsync();
        Assert.Contains("Added to Favorites", _announced);
        Assert.True(model.Rows.Single(r => r.Name == "IMG_1.jpg").IsFavorite);
        Assert.Equal(5, (await MetadataReader.ReadAsync(_lib.PathOf(@"Camera Roll\IMG_1.jpg"))).Rating);
        Assert.Equal(2, model.ItemsById["library:favorites"].Count);

        // Two selected, one already a favorite: both become favorites. Then both are, so both stop.
        Select(model, "IMG_1.jpg", "IMG_2.jpg");
        await model.ToggleFavoriteAsync();
        Assert.Contains("Removed from Favorites", _announced);
        Assert.False(model.ItemsById.ContainsKey("library:favorites"));
        Assert.Empty(_errors);
    }

    [WpfFact]
    public async Task UnfavoritingInsideFavorites_TakesThePhotoOutOfTheList()
    {
        using var model = await StartAsync();
        model.Navigate("library:favorites");
        Assert.Equal("IMG_2.jpg", Assert.Single(model.Rows).Name);
        Select(model, "IMG_2.jpg");
        await model.ToggleFavoriteAsync();
        // The album is gone, so the list goes back to All Photos rather than showing a stale place.
        Assert.Equal(SidebarIds.AllPhotos, model.CurrentSource?.Id);
    }

    [WpfFact]
    public async Task SaveCaption_WritesEveryChosenFile_AndUpdatesTheRows()
    {
        using var model = await StartAsync();
        var rows = model.Rows.Where(r => r.Name.StartsWith("IMG_1")).ToList();
        await model.SaveCaptionAsync(rows, "Family trip");
        Assert.All(rows, r => Assert.Equal("Family trip", r.Caption));
        Assert.Equal("Family trip", (await MetadataReader.ReadAsync(_lib.PathOf(@"Camera Roll\IMG_10.jpg"))).Caption);
        Assert.Contains("Caption saved", _announced);
        await model.SaveCaptionAsync(rows, "");
        Assert.Contains("Caption removed", _announced);
    }

    [WpfFact]
    public async Task WriteFailures_AreReported()
    {
        using var model = await StartAsync();
        var path = _lib.PathOf(@"Camera Roll\IMG_1.jpg");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Select(model, "IMG_1.jpg");
        await model.ToggleFavoriteAsync();
        var error = Assert.Single(_errors);
        Assert.Contains("IMG_1.jpg", error);
        Assert.DoesNotContain("Added to Favorites", _announced);
    }

    [WpfFact]
    public async Task Export_CopiesTheFiles_AndAnnouncesWhere()
    {
        using var model = await StartAsync();
        var target = Directory.CreateDirectory(_lib.PathOf("Exported")).FullName;
        await model.ExportAsync(model.Rows.Take(2).ToList(), target);
        Assert.Equal(2, Directory.GetFiles(target).Length);
        Assert.Contains("Exported 2 items to Exported", _announced);
        Assert.Null(model.Activity);
    }

    [WpfFact]
    public async Task Export_IntoAMissingFolder_ReportsAnError()
    {
        using var model = await StartAsync();
        await model.ExportAsync(model.Rows.Take(1).ToList(), _lib.PathOf("missing"));
        Assert.Single(_errors);
        Assert.Null(model.Activity);
    }

    [WpfFact]
    public async Task Columns_ChangeWhatScreenReadersHearForEachRow()
    {
        var settings = new Settings();
        using var model = await StartAsync(settings);
        var row = model.Rows.Single(r => r.Name == "IMG_10.jpg");
        Assert.DoesNotContain("holiday", row.AccessibleName);
        model.SetColumnVisible(Columns.Find("keywords")!, true);
        Assert.Contains("Keywords: holiday", row.AccessibleName);
        model.SetColumnVisible(Columns.Find("kind")!, false);
        Assert.DoesNotContain("Photo", row.AccessibleName);
        Assert.Contains("keywords", Settings.Load(_lib.PathOf(@"data\Settings.json")).ShownColumnIds());
    }

    [WpfFact]
    public async Task Gallery_MovesThroughTheList_AndStopsAtTheEnds()
    {
        using var model = await StartAsync();
        model.ViewMode = ViewMode.Gallery;
        Assert.Equal(model.Rows[0], model.FocusedRow);
        model.MoveSelection(-1);
        Assert.Equal(model.Rows[0], model.FocusedRow);
        model.MoveSelection(1);
        Assert.Equal(model.Rows[1], model.FocusedRow);
        Assert.Equal((1, 4), model.GalleryPosition);
        Assert.Contains($"{model.Rows[1].Name}, 2 of 4", _announced);
    }

    [WpfFact]
    public async Task Rescan_FindsNewFiles_AndKeepsDetailsAlreadyRead()
    {
        using var model = await StartAsync();
        _lib.AddJpeg(@"Camera Roll\IMG_99.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2025, 1, 1), Title = "New one" });
        await model.RescanAsync();
        await model.DetailFillTask;
        Assert.Equal(5, model.Rows.Count);
        Assert.Equal("New one", model.Rows.Single(r => r.Name == "IMG_99.jpg").Caption);
        Assert.Equal("Breakfast", model.Rows.Single(r => r.Name == "IMG_1.jpg").Caption);
        Assert.Contains("2025", model.Sections[1].Children.Select(c => c.Title));
    }

    [WpfFact]
    public async Task ASecondRun_UsesTheCache()
    {
        using (await StartAsync()) { }
        var cache = new MetadataCache(_lib.PathOf(@"data\Metadata.json"));
        Assert.Equal(4, cache.Count);
    }

    [WpfFact]
    public async Task AddAndRemoveFolder_ChangeTheLibrary_WithoutTouchingTheDisk()
    {
        using var other = new TestLibrary();
        other.AddJpeg("elsewhere.jpg");
        var settings = new Settings();
        using var model = await StartAsync(settings);
        await model.AddFolderAsync(other.Root);
        Assert.Equal(5, model.ItemsById[SidebarIds.AllPhotos].Count);
        Assert.Equal(2, model.Sections[2].Children.Count);

        model.Navigate(SidebarIds.Folder(other.Root));
        Assert.Equal(other.Root, model.CurrentRoot);
        await model.RemoveFolderAsync(other.Root);
        Assert.Equal(4, model.ItemsById[SidebarIds.AllPhotos].Count);
        Assert.True(File.Exists(other.PathOf("elsewhere.jpg")));
        Assert.Empty(settings.ExtraFolders);
    }

    [WpfFact]
    public async Task AFileEditedElsewhere_IsReadAgain_NotLeftWithoutDetails()
    {
        using var model = await StartAsync();
        var path = _lib.PathOf(@"Camera Roll\IMG_1.jpg");
        // Another app changes the title, as File Explorer would.
        var info = new FileInfo(path);
        await MetadataWriter.SetCaptionAsync([new MediaItem(path, info.Length, info.CreationTime, info.LastWriteTime)], "Edited elsewhere");
        File.SetLastWriteTime(path, DateTime.Now.AddMinutes(1));
        await model.RescanAsync();
        await model.DetailFillTask;
        var row = model.Rows.Single(r => r.Name == "IMG_1.jpg");
        Assert.Equal("Edited elsewhere", row.Caption);
        Assert.Equal(new DateTime(2024, 3, 5, 9, 0, 0), row.Date);
    }

    [WpfFact]
    public async Task RefreshDuringTheFirstLoad_StillOpensAllPhotos()
    {
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        using (model)
        {
            var first = model.StartAsync();
            var second = model.RescanAsync();
            await Task.WhenAll(first, second);
            Assert.Equal(SidebarIds.AllPhotos, model.CurrentSource?.Id);
            Assert.Equal(4, model.Rows.Count);
        }
    }

    [WpfFact]
    public async Task Navigate_BuildsTheRowsOnce()
    {
        using var model = await StartAsync();
        var builds = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryModel.Rows)) builds++; };
        model.Navigate(SidebarIds.Year(2024));
        Assert.Equal(1, builds);
    }

    [WpfFact]
    public async Task UnfavoritingInsideFavorites_SelectsTheNextFavorite()
    {
        _lib.AddJpeg(@"Camera Roll\IMG_3.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 1), Rating = 5 });
        using var model = await StartAsync();
        model.Navigate("library:favorites");
        Assert.Equal(["IMG_2.jpg", "IMG_3.jpg"], model.Rows.Select(r => r.Name));
        Select(model, "IMG_2.jpg");
        await model.ToggleFavoriteAsync();
        Assert.Equal("IMG_3.jpg", Assert.Single(model.Rows).Name);
        Assert.Equal("IMG_3.jpg", Assert.Single(model.SelectedRows).Name);
    }

    [WpfFact]
    public async Task ASecondWriteWhileOneIsRunning_IsRefused()
    {
        using var model = await StartAsync();
        Select(model, "IMG_1.jpg");
        var first = model.ToggleFavoriteAsync();
        var second = model.SetRatingAsync(2);
        await Task.WhenAll(first, second);
        Assert.Contains("Still saving the last change", _announced);
        Assert.Equal(5, (await MetadataReader.ReadAsync(_lib.PathOf(@"Camera Roll\IMG_1.jpg"))).Rating);
        Assert.False(model.IsWriting);
    }

    [WpfFact]
    public async Task Search_SpeaksHowManyResults()
    {
        using var model = await StartAsync();
        model.SearchText = "IMG";
        Assert.Contains("3 results", _announced);
        model.SearchText = "zzz";
        Assert.Contains("No results", _announced);
    }

    [WpfFact]
    public async Task ARescanThatFindsNothingNew_LeavesTheListAlone()
    {
        using var model = await StartAsync();
        var builds = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryModel.Rows)) builds++; };
        await model.RescanAsync();
        Assert.Equal(0, builds);
    }

    [WpfFact]
    public async Task FocusedRow_IsTheFirstSelectedInListOrder()
    {
        using var model = await StartAsync();
        model.SelectedRows = [model.Rows[3], model.Rows[1]];
        Assert.Equal(model.Rows[1], model.FocusedRow);
        Assert.Equal((1, 4), model.GalleryPosition);
    }

    [WpfFact]
    public async Task OpeningNothing_DoesNothing()
    {
        using var model = await StartAsync();
        model.SelectedRows = [];
        model.Open();
        model.Copy();
        await model.ToggleFavoriteAsync();
        Assert.Empty(_errors);
        Assert.Equal(SidebarIds.AllPhotos, model.CurrentSource?.Id);
    }
}
