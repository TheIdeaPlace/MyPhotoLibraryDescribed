using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Tests;

/// <summary>Automatic descriptions against a real library, with a stand-in for Windows AI so they run anywhere.</summary>
[Collection(SharedState.Name)]
public sealed class AutoDescriberTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];
    private readonly List<string> _described = [];

    public AutoDescriberTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 3) });
        _lib.AddJpeg("b.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 2) });
        _lib.AddJpeg("c.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 1) });
        _lib.AddFile("clip.mp4", "video");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private static readonly AIReadiness Ready = new(true, "Ready", null);

    private async Task<(LibraryModel Model, AutoDescriber Auto, DescriptionStore Store)> StartAsync(
        AIReadiness? readiness = null, Func<MediaItem, Task<string>>? describe = null, Settings? settings = null)
    {
        var model = new LibraryModel(settings ?? new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        model.SelectionRequested += (ids, _) => model.SelectedRows = model.Rows.Where(r => ids.Contains(r.Id)).ToList();
        await model.StartAsync();
        await model.DetailFillTask;
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var auto = new AutoDescriber(model, store,
            async (item, kind, _) =>
            {
                _described.Add($"{item.Name}:{kind}");
                return describe is null ? $"A picture called {item.Name}." : await describe(item);
            },
            _ => Task.FromResult(readiness ?? Ready), TimeSpan.FromMilliseconds(30));
        return (model, auto, store);
    }

    private static void Select(LibraryModel model, string name) => model.SelectedRows = model.Rows.Where(r => r.Name == name).ToList();

    private static async Task Settle(AutoDescriber auto)
    {
        await Task.Delay(120);
        await auto.Idle;
    }

    [WpfFact]
    public async Task ArrowingToAPhoto_DescribesItAndSpeaksIt()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Contains("A picture called a.jpg.", _announced);
            Assert.Equal("A picture called a.jpg.", model.FocusedRow!.Description);
            Assert.Equal(["a.jpg:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task ArrowingQuicklyPastPhotos_OnlyDescribesWhereYouStop()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            Select(model, "b.jpg");
            Select(model, "c.jpg");
            await Settle(auto);
            Assert.Equal(["c.jpg:accessible"], _described);
            Assert.DoesNotContain("A picture called a.jpg.", _announced);
        }
    }

    [WpfFact]
    public async Task GoingBackToAPhoto_SpeaksTheSavedDescription_WithoutDescribingAgain()
    {
        var (model, auto, store) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Select(model, "b.jpg");
            await Settle(auto);
            _announced.Clear();
            Select(model, "a.jpg");
            Assert.Contains("A picture called a.jpg.", _announced);
            Assert.Equal(["a.jpg:accessible", "b.jpg:accessible"], _described);
            Assert.NotNull(store.Get(model.FocusedRow!.Item!, "accessible"));
        }
    }

    [WpfFact]
    public async Task MovingOnWhileDescribing_SavesItButDoesntSpeakIt()
    {
        var release = new TaskCompletionSource();
        var (model, auto, store) = await StartAsync(describe: async item =>
        {
            if (item.Name == "a.jpg") await release.Task;
            return $"Slow {item.Name}";
        });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Task.Delay(120);
            Select(model, "b.jpg");
            await Task.Delay(120);
            release.SetResult();
            await Settle(auto);
            await Settle(auto);
            Assert.DoesNotContain("Slow a.jpg", _announced);
            Assert.Contains("Slow b.jpg", _announced);
            var a = model.Rows.Single(r => r.Name == "a.jpg").Item!;
            Assert.Equal("Slow a.jpg", store.Get(a, "accessible"));
        }
    }

    [WpfFact]
    public async Task ARefreshThatReselectsTheSamePhoto_DoesntSpeakAgain()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            _announced.Clear();
            model.SelectedRows = model.Rows.Where(r => r.Name == "a.jpg").ToList();
            await Settle(auto);
            Assert.Empty(_announced);
        }
    }

    [WpfFact]
    public async Task VideosFoldersAndSeveralSelected_AreNotDescribed()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "clip.mp4");
            await Settle(auto);
            model.SelectedRows = model.Rows.Where(r => r.Name is "a.jpg" or "b.jpg").ToList();
            await Settle(auto);
            Assert.Empty(_described);
        }
    }

    [WpfFact]
    public async Task WhenWindowsAIIsntAvailable_ItSaysSoOnce()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Select(model, "b.jpg");
            await Settle(auto);
            Assert.Single(_announced, a => a.Contains("Needs a Copilot+ PC."));
            Assert.Empty(_described);
        }
    }

    [WpfFact]
    public async Task TurnedOff_NothingIsDescribed_AndTheSettingIsSaved()
    {
        var settings = new Settings();
        var (model, auto, _) = await StartAsync(settings: settings);
        using (model) using (auto)
        {
            auto.Enabled = false;
            Assert.Contains("Automatic descriptions off", _announced);
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Empty(_described);
            Assert.False(Settings.Load(_lib.PathOf(@"data\Settings.json")).AutoDescribe);

            auto.Enabled = true;
            await Settle(auto);
            Assert.Equal(["a.jpg:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task ADescriptionError_IsSpokenForThePhotoYoureOn()
    {
        var (model, auto, _) = await StartAsync(describe: _ => throw new DescriptionException("content_filtered", "Windows' content filter declined to describe this picture."));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Contains("Windows' content filter declined to describe this picture.", _announced);
        }
    }

    [WpfFact]
    public async Task UsesTheChosenDescriptionKind()
    {
        var (model, auto, _) = await StartAsync(settings: new Settings { DescriptionKind = "brief" });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Equal(["a.jpg:brief"], _described);
        }
    }
}

public class DescriptionStoreTests
{
    [Fact]
    public void SavedDescriptions_SurviveARestart_AndAnEditedFileIsDescribedAgain()
    {
        using var lib = new TestLibrary();
        var file = lib.PathOf(@"data\Descriptions.json");
        var date = new DateTime(2020, 1, 1);
        var item = new MediaItem(@"C:\p\a.jpg", 10, date, date);
        new DescriptionStore(file).Put(item, "brief", "A cat.");
        var start = DateTime.UtcNow;
        while (!File.Exists(file) && DateTime.UtcNow - start < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        Thread.Sleep(100);

        var reloaded = new DescriptionStore(file);
        Assert.Equal("A cat.", reloaded.Get(item, "brief"));
        Assert.Null(reloaded.Get(item, "detailed"));
        Assert.Null(reloaded.Get(new MediaItem(@"C:\p\a.jpg", 11, date, date), "brief"));
    }
}
