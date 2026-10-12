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

    private int _prepared;

    private async Task<(LibraryModel Model, AutoDescriber Auto, DescriptionStore Store)> StartAsync(
        AIReadiness? readiness = null, Func<MediaItem, Task<string>>? describe = null, Settings? settings = null,
        AIReadiness? afterPrepare = null, AutoDescriber.DescribeFunc? describeAhead = null)
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var model = new LibraryModel(settings ?? new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1)) { Descriptions = store };
        model.SelectionRequested += (ids, _) => model.SelectedRows = model.Rows.Where(r => ids.Contains(r.Id)).ToList();
        await model.StartAsync();
        await model.DetailFillTask;
        var auto = new AutoDescriber(model, store,
            async (item, kind, _) =>
            {
                _described.Add($"{item.Name}:{kind}");
                return describe is null ? $"A picture called {item.Name}." : await describe(item);
            },
            _ => Task.FromResult(_prepared > 0 ? afterPrepare ?? Ready : readiness ?? Ready),
            _ => { _prepared++; return Task.FromResult(afterPrepare ?? Ready); },
            TimeSpan.FromMilliseconds(30), describeAhead);
        return (model, auto, store);
    }

    private static void Select(LibraryModel model, string name) => model.SelectedRows = model.Rows.Where(r => r.Name == name).ToList();

    private static Task Settle(AutoDescriber auto) => auto.WhenIdleAsync();

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
            // After the same pause as a new description, so the row's name is heard first.
            Assert.DoesNotContain("A picture called a.jpg.", _announced);
            await Settle(auto);
            Assert.Contains("A picture called a.jpg.", _announced);
            // c.jpg was described ahead while stepping from a to b; going back doesn't look ahead to b again.
            Assert.Equal(["a.jpg:accessible", "b.jpg:accessible", "c.jpg:accessible"], _described);
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
            await WaitUntil(() => _described.Count == 1);
            Select(model, "b.jpg");
            await Task.Delay(100);
            release.SetResult();
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
    public async Task WhenWindowsAIIsntAvailable_ArrowingStaysQuiet_ButTurningItOnSaysWhy()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Select(model, "b.jpg");
            await Settle(auto);
            Assert.DoesNotContain(_announced, a => a.Contains("Copilot+"));
            Assert.Empty(_described);

            auto.Enabled = true;
            await Settle(auto);
            Assert.Single(_announced, a => a.Contains("Needs a Copilot+ PC."));
        }
    }

    [WpfFact]
    public async Task AModelNotDownloadedYet_IsntDownloadedByArrowing_OnlyByTurningItOn()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(true, "NotReady", null));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Equal(0, _prepared);
            Assert.Empty(_described);

            auto.Enabled = true;
            await Settle(auto);
            Assert.Equal(1, _prepared);
            Assert.Contains(_announced, a => a.Contains("Getting Windows' image description model ready"));
            Assert.Equal(["a.jpg:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task APrepareThatDoesntFinish_IsReportedNotRetriedForEveryPhoto()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(true, "NotReady", null),
            afterPrepare: new AIReadiness(true, "NotReady", "Still downloading."));
        using (model) using (auto)
        {
            auto.Enabled = true;
            await Settle(auto);
            Select(model, "a.jpg");
            await Settle(auto);
            Select(model, "b.jpg");
            await Settle(auto);
            Assert.Equal(1, _prepared);
            Assert.Empty(_described);
            Assert.Contains(_announced, a => a.Contains("Still downloading."));
        }
    }

    [WpfFact]
    public async Task NotOnThePhoto_NothingIsDescribedOrSpoken()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            var onPhoto = false;
            auto.CanSpeak = () => onPhoto;
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Empty(_described);

            // Back in the list, on the same photo: now it's described.
            onPhoto = true;
            auto.Refocused();
            await Settle(auto);
            Assert.Equal(["a.jpg:accessible"], _described);
            Assert.Contains("A picture called a.jpg.", _announced);

            // Focus comes back again (after a dialog, say): already spoken, so not again.
            _announced.Clear();
            auto.Refocused();
            await Settle(auto);
            Assert.Empty(_announced);
        }
    }

    [WpfFact]
    public async Task MovingAwayMidDescription_SavesItWithoutSpeaking()
    {
        var release = new TaskCompletionSource();
        var onPhoto = true;
        var (model, auto, store) = await StartAsync(describe: async item => { await release.Task; return "Done"; });
        using (model) using (auto)
        {
            auto.CanSpeak = () => onPhoto;
            Select(model, "a.jpg");
            await WaitUntil(() => _described.Count == 1);
            onPhoto = false;
            release.SetResult();
            await Settle(auto);
            Assert.DoesNotContain("Done", _announced);
            Assert.Equal("Done", store.Get(model.FocusedRow!.Item!, "accessible"));
        }
    }

    [WpfFact]
    public async Task TurningItOffMidDescription_DoesntSpeakIt()
    {
        var release = new TaskCompletionSource();
        var (model, auto, _) = await StartAsync(describe: async _ => { await release.Task; return "Late"; });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await WaitUntil(() => _described.Count == 1);
            auto.Enabled = false;
            release.SetResult();
            await Settle(auto);
            Assert.DoesNotContain("Late", _announced);
        }
    }

    [WpfFact]
    public async Task AnUnexpectedFailure_IsSpoken_AndTheNextPhotoStillWorks()
    {
        var (model, auto, _) = await StartAsync(describe: item =>
            item.Name == "a.jpg" ? throw new IOException("The file is in use.") : Task.FromResult($"Fine {item.Name}"));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Contains(_announced, a => a.Contains("The file is in use."));
            Select(model, "b.jpg");
            await Settle(auto);
            Assert.Contains("Fine b.jpg", _announced);
        }
    }

    [WpfFact]
    public async Task AnEmptyDescription_IsntSaved()
    {
        var (model, auto, store) = await StartAsync(describe: _ => Task.FromResult(""));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Contains("Windows returned an empty description.", _announced);
            Assert.Null(store.Get(model.FocusedRow!.Item!, "accessible"));
        }
    }

    [WpfFact]
    public async Task AfterTheListIsRebuilt_TheDescriptionIsBackOnTheRow_WithoutBeingSpokenAgain()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            _announced.Clear();
            model.SortBy("name");
            await Settle(auto);
            Assert.Equal("A picture called a.jpg.", model.FocusedRow!.Description);
            Assert.DoesNotContain("A picture called a.jpg.", _announced);
        }
    }

    [WpfFact]
    public async Task FavoritingAPhoto_KeepsItsDescription()
    {
        var (model, auto, store) = await StartAsync();
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            await model.ToggleFavoriteAsync();
            Assert.Equal("A picture called a.jpg.", store.Get(model.Rows.Single(r => r.Name == "a.jpg").Item!, "accessible"));
        }
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(10);
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

    // MARK: Describe Photo (Ctrl+Shift+D)

    private static readonly Settings ManualOnly = new() { AutoDescribe = false };

    [WpfFact]
    public async Task DescribeNow_WithAutomaticOff_DescribesSpeaksAndSaves_ThenSpeaksTheSavedOne()
    {
        var (model, auto, store) = await StartAsync(settings: ManualOnly);
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await Settle(auto);
            Assert.Empty(_described);
            var row = model.FocusedRow!;
            await auto.DescribeNowAsync(row, () => true);
            Assert.Equal(["a.jpg:accessible"], _described);
            Assert.Contains("Describing a.jpg", _announced);
            Assert.Equal("A picture called a.jpg.", _announced[^1]);
            Assert.Equal("A picture called a.jpg.", row.Description);
            Assert.Equal("A picture called a.jpg.", store.Get(row.Item!, "accessible"));

            _announced.Clear();
            await auto.DescribeNowAsync(row, () => true);
            Assert.Single(_described);
            Assert.Equal(["A picture called a.jpg."], _announced);
        }
    }

    [WpfFact]
    public async Task DescribeNow_UsesTheChosenDescriptionType()
    {
        var (model, auto, _) = await StartAsync(settings: new Settings { AutoDescribe = false, DescriptionKind = "brief" });
        using (model) using (auto)
        {
            Select(model, "b.jpg");
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Equal(["b.jpg:brief"], _described);
        }
    }

    [WpfFact]
    public async Task DescribeNow_OnAVideo_SaysItCant()
    {
        var (model, auto, _) = await StartAsync(settings: ManualOnly);
        using (model) using (auto)
        {
            Select(model, "clip.mp4");
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Empty(_described);
            Assert.Contains("Videos can't be described.", _announced);
        }
    }

    [WpfFact]
    public async Task DescribeNow_GetsTheModelReady_SinceYouAsked()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(false, "NotReady", "Needs downloading."), settings: ManualOnly);
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Equal(1, _prepared);
            Assert.Contains(_announced, a => a.StartsWith("Getting Windows' image description model ready"));
            Assert.Equal(["a.jpg:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task DescribeNow_WithoutWindowsAI_SaysWhy()
    {
        var (model, auto, _) = await StartAsync(new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."), settings: ManualOnly);
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Empty(_described);
            Assert.Contains("This photo can't be described. Needs a Copilot+ PC.", _announced);
        }
    }

    [WpfFact]
    public async Task DescribeNow_AfterYouMovedOn_SavesItButDoesntSpeakIt()
    {
        var (model, auto, store) = await StartAsync(settings: ManualOnly);
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var row = model.FocusedRow!;
            await auto.DescribeNowAsync(row, () => false);
            Assert.DoesNotContain("A picture called a.jpg.", _announced);
            Assert.Equal("A picture called a.jpg.", store.Get(row.Item!, "accessible"));
        }
    }

    [WpfFact]
    public async Task DescribeNow_PressedTwice_DescribesOnce()
    {
        var release = new TaskCompletionSource();
        var (model, auto, _) = await StartAsync(settings: ManualOnly, describe: async item => { await release.Task; return "Once."; });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var row = model.FocusedRow!;
            var first = auto.DescribeNowAsync(row, () => true);
            await WaitUntil(() => _described.Count == 1);
            var second = auto.DescribeNowAsync(row, () => true);
            Assert.Contains("Still describing a.jpg", _announced);
            release.SetResult();
            await Task.WhenAll(first, second);
            Assert.Single(_described);
            Assert.Single(_announced, a => a == "Once.");
        }
    }

    [WpfFact]
    public async Task DescribeNow_WhileAutomaticIsDescribingThatPhoto_WaitsForItAndSpeaksIt()
    {
        var release = new TaskCompletionSource();
        var (model, auto, _) = await StartAsync(describe: async item => { await release.Task; return "Shared."; });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await WaitUntil(() => _described.Count == 1);
            var manual = auto.DescribeNowAsync(model.FocusedRow!, () => true);
            release.SetResult();
            await manual;
            await Settle(auto);
            Assert.Single(_described);
            Assert.Single(_announced, a => a == "Shared.");
        }
    }

    [WpfFact]
    public async Task DescribeAgain_ReplacesTheSavedDescription_AndSaysSo()
    {
        var (model, auto, store) = await StartAsync(settings: ManualOnly, describe: _ => Task.FromResult("A fresh look."));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var row = model.FocusedRow!;
            store.Put(row.Item!, "accessible", "An old description.");

            await auto.DescribeNowAsync(row, () => true, again: true);
            Assert.Equal(["a.jpg:accessible"], _described);
            Assert.Contains("Describing a.jpg again", _announced);
            Assert.Equal("A fresh look.", _announced[^1]);
            Assert.Equal("A fresh look.", store.Get(row.Item!, "accessible"));
            Assert.Equal("A fresh look.", row.Description);

            // Describe Photo afterwards speaks the new one without describing.
            _announced.Clear();
            await auto.DescribeNowAsync(row, () => true);
            Assert.Single(_described);
            Assert.Equal(["A fresh look."], _announced);
        }
    }

    [WpfFact]
    public async Task DescribeAgain_WhileAutomaticIsDescribingThatPhoto_KeepsTheNewerDescription()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var (model, auto, store) = await StartAsync(describe: async _ =>
        {
            var call = ++calls;
            if (call == 1) await release.Task;
            return call == 1 ? "The automatic one." : "The one asked for again.";
        });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await WaitUntil(() => _described.Count == 1);
            var again = auto.DescribeNowAsync(model.FocusedRow!, () => true, again: true);
            release.SetResult();
            await again;
            await Settle(auto);
            Assert.Equal(2, _described.Count);
            Assert.Equal("The one asked for again.", store.Get(model.FocusedRow!.Item!, "accessible"));
            Assert.DoesNotContain("The automatic one.", _announced);
            Assert.Equal("The one asked for again.", _announced[^1]);
        }
    }

    [WpfFact]
    public async Task DescribeAgain_ArrowingAwayAndBack_DoesntSayTheOldDescription_OrAskTwice()
    {
        var release = new TaskCompletionSource();
        var (model, auto, store) = await StartAsync(describe: async _ => { await release.Task; return "The new one."; });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var row = model.FocusedRow!;
            store.Put(row.Item!, "accessible", "The old one.");
            await Settle(auto);
            _announced.Clear();

            var again = auto.DescribeNowAsync(row, () => true, again: true);
            await WaitUntil(() => _described.Count == 1);
            Select(model, "b.jpg");
            Select(model, "a.jpg");
            await Task.Delay(200);
            Assert.DoesNotContain("The old one.", _announced);
            release.SetResult();
            await again;
            await Settle(auto);

            Assert.DoesNotContain("The old one.", _announced);
            Assert.Equal(1, _described.Count(d => d.StartsWith("a.jpg")));
            Assert.Equal("The new one.", store.Get(row.Item!, "accessible"));
            Assert.Equal("The new one.", _announced[^1]);
        }
    }

    [WpfFact]
    public async Task DescribeAgain_TwiceOnOnePhoto_AfterAnotherPhoto_StillSaysStillDescribing()
    {
        var release = new TaskCompletionSource();
        var (model, auto, _) = await StartAsync(settings: ManualOnly, describe: async _ => { await release.Task; return "Done."; });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var a = model.FocusedRow!;
            var first = auto.DescribeNowAsync(a, () => true, again: true);
            Select(model, "b.jpg");
            var b = auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Select(model, "a.jpg");
            var second = auto.DescribeNowAsync(a, () => true, again: true);
            Assert.Contains("Still describing a.jpg", _announced);
            Assert.Same(first, second);
            release.SetResult();
            await Task.WhenAll(first, b);
            Assert.Equal(1, _described.Count(d => d.StartsWith("a.jpg")));
        }
    }

    [WpfFact]
    public async Task ACorrectionMadeWhileDescribing_IsKept_ButAnAgreedDescribeAgainReplacesIt()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var (model, auto, store) = await StartAsync(describe: async _ =>
        {
            if (++calls == 1) await release.Task;
            return calls == 1 ? "Windows' first try." : "Windows, asked again.";
        });
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            var row = model.FocusedRow!;
            await WaitUntil(() => _described.Count == 1);
            Assert.True(auto.IsDescribing(row.Item!.Path));
            store.Put(row.Item!, "accessible", "My correction.", edited: true);
            release.SetResult();
            await Settle(auto);
            Assert.False(auto.IsDescribing(row.Item!.Path));
            Assert.Equal("My correction.", store.Get(row.Item!, "accessible"));
            Assert.DoesNotContain("Windows' first try.", _announced);

            await auto.DescribeNowAsync(row, () => true, again: true, replacing: "My correction.");
            Assert.Equal("Windows, asked again.", store.Get(row.Item!, "accessible"));
            Assert.False(store.IsEdited(row.Item!, "accessible"));
        }
    }

    [WpfFact]
    public async Task DescribeNow_ACaughtFailureIsSpoken()
    {
        var (model, auto, _) = await StartAsync(settings: ManualOnly,
            describe: _ => throw new DescriptionException("content_filtered", "Windows declined to describe this photo."));
        using (model) using (auto)
        {
            Select(model, "a.jpg");
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Equal("Windows declined to describe this photo.", _announced[^1]);
            Assert.False(auto.IsBusy);
        }
    }

    // MARK: Describing ahead (#64)

    /// <summary>The three photos in list order, which these tests step through: they're next to each other.</summary>
    private static string[] PhotoOrder(LibraryModel model)
    {
        var photos = model.Rows.Where(r => r.Name.EndsWith(".jpg")).ToList();
        var first = model.Rows.IndexOf(photos[0]);
        Assert.Equal([first, first + 1, first + 2], photos.Select(r => model.Rows.IndexOf(r)));
        return photos.Select(r => r.Name).ToArray();
    }

    [WpfFact]
    public async Task SteppingToAPhoto_DescribesTheNextOneAhead_Quietly_AndArrivingSpeaksItWithoutAskingAgain()
    {
        var (model, auto, store) = await StartAsync();
        using (model) using (auto)
        {
            var (first, second, third) = PhotoOrder(model) is var o ? (o[0], o[1], o[2]) : default;
            Select(model, first);
            await Settle(auto);
            // Landing on a photo (not a step) doesn't look ahead.
            Assert.Equal([$"{first}:accessible"], _described);
            Select(model, second);
            await Settle(auto);
            Assert.Equal([$"{first}:accessible", $"{second}:accessible", $"{third}:accessible"], _described);
            Assert.DoesNotContain($"A picture called {third}.", _announced);
            var ahead = model.Rows.Single(r => r.Name == third);
            Assert.Equal($"A picture called {third}.", store.Get(ahead.Item!, "accessible"));
            Assert.Equal($"A picture called {third}.", ahead.Description);

            Select(model, third);
            await Settle(auto);
            Assert.Contains($"A picture called {third}.", _announced);
            Assert.Single(_described, d => d.StartsWith(third));
        }
    }

    [WpfFact]
    public async Task SteppingBack_LooksAheadBackwards()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[2]);
            await Settle(auto);
            Select(model, o[1]);
            await Settle(auto);
            Assert.Equal([$"{o[2]}:accessible", $"{o[1]}:accessible", $"{o[0]}:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task JumpingToAPhoto_DoesntLookAhead()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[2]);
            await Settle(auto);
            Assert.Equal([$"{o[0]}:accessible", $"{o[2]}:accessible"], _described);
        }
    }

    [WpfFact]
    public async Task ARefreshBetweenSteps_StillCountsAsAStep()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[0]);
            await Settle(auto);
            // A caption write or rescan replaces the rows without moving them.
            await model.RescanAsync();
            Select(model, o[1]);
            await Settle(auto);
            Assert.Contains($"{o[2]}:accessible", _described);
        }
    }

    [WpfFact]
    public async Task OnEnergySaverOrAwayFromTheList_NothingIsDescribedAhead()
    {
        var (model, auto, _) = await StartAsync();
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            auto.MayDescribeAhead = () => false;
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[1]);
            await Settle(auto);
            Assert.DoesNotContain($"{o[2]}:accessible", _described);
        }
    }

    [WpfFact]
    public async Task VideosAndDescribedPhotos_ArentDescribedAhead()
    {
        var (model, auto, store) = await StartAsync();
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            store.Put(model.Rows.Single(r => r.Name == o[2]).Item!, "accessible", "Already described.");
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[1]);
            await Settle(auto);
            Assert.DoesNotContain($"{o[2]}:accessible", _described);

            // From the video's neighbour toward it.
            var clip = model.Rows.FindIndex(r => r.Name == "clip.mp4");
            if (clip > 0 && model.Rows[clip - 1].Item is { IsVideo: false } before && clip >= 2 && model.Rows[clip - 2].Item is { IsVideo: false } start)
            {
                _described.Clear();
                Select(model, start.Name);
                await Settle(auto);
                Select(model, before.Name);
                await Settle(auto);
                Assert.DoesNotContain(_described, d => d.StartsWith("clip"));
            }
        }
    }

    [WpfFact]
    public async Task LandingElsewhere_DropsTheWorkAhead_BeforeWindowsIsAsked_AndArrivingLaterDescribesIt()
    {
        var turn = new TaskCompletionSource();
        var asked = new List<string>();
        var (model, auto, store) = await StartAsync(describeAhead: async (item, kind, drop) =>
        {
            await turn.Task.WaitAsync(drop);   // decoding, or waiting for the helper
            asked.Add(item.Name);
            return $"Ahead {item.Name}";
        });
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[1]);
            await WaitUntil(() => auto.IsDescribing(model.Rows.Single(r => r.Name == o[2]).Item!.Path));
            Select(model, o[0]);
            await Settle(auto);
            turn.SetResult();
            Assert.Empty(asked);
            Assert.Null(store.Get(model.Rows.Single(r => r.Name == o[2]).Item!, "accessible"));

            Select(model, o[2]);
            await Settle(auto);
            Assert.Contains($"A picture called {o[2]}.", _announced);
        }
    }

    [WpfFact]
    public async Task ArrivingWhileItsBeingDescribedAhead_WaitsForThat_AndSpeaksIt()
    {
        var finish = new TaskCompletionSource();
        var (model, auto, store) = await StartAsync(describeAhead: async (item, kind, _) =>
        {
            _described.Add($"{item.Name}:ahead");
            await finish.Task;   // Windows has it: dropping no longer stops it
            return $"Ahead {item.Name}";
        });
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[1]);
            await WaitUntil(() => _described.Contains($"{o[2]}:ahead"));
            Select(model, o[2]);
            await Task.Delay(150);
            Assert.True(auto.IsBusy);
            finish.SetResult();
            await Settle(auto);
            Assert.Contains($"Ahead {o[2]}", _announced);
            Assert.DoesNotContain($"{o[2]}:accessible", _described);
        }
    }

    [WpfFact]
    public async Task DescribePhoto_OnAPhotoBeingDescribedAhead_WaitsForThat()
    {
        var finish = new TaskCompletionSource();
        var (model, auto, _) = await StartAsync(describeAhead: async (item, kind, _) =>
        {
            _described.Add($"{item.Name}:ahead");
            await finish.Task;
            return $"Ahead {item.Name}";
        });
        using (model) using (auto)
        {
            var o = PhotoOrder(model);
            Select(model, o[0]);
            await Settle(auto);
            Select(model, o[1]);
            await WaitUntil(() => _described.Contains($"{o[2]}:ahead"));
            var row = model.Rows.Single(r => r.Name == o[2]);
            var asking = auto.DescribeNowAsync(row, () => true);
            await Task.Delay(100);
            finish.SetResult();
            await asking;
            Assert.Contains($"Ahead {o[2]}", _announced);
            Assert.DoesNotContain($"{o[2]}:accessible", _described);
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
