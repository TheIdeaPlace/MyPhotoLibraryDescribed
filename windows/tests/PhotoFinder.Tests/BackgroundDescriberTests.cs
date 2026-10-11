using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Tests;

/// <summary>Describing added folders behind the scenes, with a stand-in for Windows AI so it runs anywhere.</summary>
[Collection(SharedState.Name)]
public sealed class BackgroundDescriberTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly TestLibrary _added = new();
    private readonly List<string> _announced = [];
    private readonly List<string> _described = [];

    public BackgroundDescriberTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("home.jpg");
        _added.AddJpeg("a.jpg");
        _added.AddJpeg(@"Day 2\b.jpg");
        _added.AddFile("clip.mp4", "video");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
        _added.Dispose();
    }

    private static readonly AIReadiness Ready = new(true, "Ready", null);

    private async Task<(LibraryModel Model, BackgroundDescriber Background, DescriptionStore Store)> StartAsync(
        bool enabled, AIReadiness? readiness = null, Func<MediaItem, Task<string>>? describe = null)
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var settings = new Settings { DescribeInBackground = enabled };
        var model = new LibraryModel(settings, _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1)) { Descriptions = store };
        await model.StartAsync();
        var background = new BackgroundDescriber(model, store,
            async (item, kind, _) =>
            {
                _described.Add(item.Name);
                return describe is null ? $"About {item.Name}" : await describe(item);
            },
            _ => Task.FromResult(readiness ?? Ready), _ => Task.FromResult(Ready));
        return (model, background, store);
    }

    [WpfFact]
    public async Task AddingAFolder_DescribesItsPhotos_IncludingSubfolders_ButNotVideos()
    {
        var (model, background, store) = await StartAsync(enabled: true);
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.Idle;
            Assert.Equal(["a.jpg", "b.jpg"], _described.Order());
            var a = model.AllItems.Single(i => i.Name == "a.jpg");
            Assert.Equal("About a.jpg", store.Get(a, "accessible"));
            Assert.Contains(_announced, s => s.StartsWith("Describing 2 photos from"));
            Assert.Contains(_announced, s => s.StartsWith("Finished describing the photos in"));
            Assert.Null(model.BackgroundProgress);
            // The photo already in the library before isn't part of the added folder.
            Assert.DoesNotContain("home.jpg", _described);
        }
    }

    [WpfFact]
    public async Task TurnedOff_AddingAFolderDescribesNothing()
    {
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.Idle;
            Assert.Empty(_described);
        }
    }

    [WpfFact]
    public async Task PhotosAlreadyDescribed_AreSkipped()
    {
        var (model, background, store) = await StartAsync(enabled: true);
        using (model) using (background)
        {
            var info = new FileInfo(_added.PathOf("a.jpg"));
            store.Put(new MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime), "accessible", "Already");
            await model.AddFolderAsync(_added.Root);
            await background.Idle;
            Assert.Equal(["b.jpg"], _described);
        }
    }

    [WpfFact]
    public async Task OnePhotoFailing_DoesntStopTheRest()
    {
        var (model, background, store) = await StartAsync(enabled: true,
            describe: item => item.Name == "a.jpg" ? throw new DescriptionException("content_filtered", "No") : Task.FromResult("Fine"));
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.Idle;
            Assert.Equal(2, _described.Count);
            Assert.Equal("Fine", store.Get(model.AllItems.Single(i => i.Name == "b.jpg"), "accessible"));
        }
    }

    [WpfFact]
    public async Task WithoutWindowsAI_AddingAFolderStaysQuiet_ButTurningItOnSaysWhy()
    {
        var (model, background, _) = await StartAsync(enabled: true, readiness: new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."));
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.Idle;
            Assert.Empty(_described);
            Assert.DoesNotContain(_announced, s => s.Contains("Copilot+"));
            Assert.Null(model.BackgroundProgress);

            background.Enabled = true;
            await Task.Delay(100);
            Assert.Contains(_announced, s => s.Contains("Needs a Copilot+ PC."));
        }
    }

    [WpfFact]
    public async Task ProgressShowsInTheStatusBar_WhileDescribing()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            Assert.Equal("Describing photos in the background, 1 of 2", model.BackgroundProgress);
            Assert.Contains("Describing photos in the background, 1 of 2", model.StatusBarText);
            release.SetResult();
            await background.Idle;
            Assert.DoesNotContain("background", model.StatusBarText);
        }
    }

    [WpfFact]
    public async Task TheSetting_IsSaved()
    {
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            background.Enabled = true;
            Assert.True(Settings.Load(_lib.PathOf(@"data\Settings.json")).DescribeInBackground);
            Assert.Contains("Folders you add will be described in the background", _announced);
        }
    }
}
