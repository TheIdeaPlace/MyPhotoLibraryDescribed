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
        bool enabled, AIReadiness? readiness = null, Func<MediaItem, Task<string>>? describe = null, Func<AIReadiness>? check = null, string? failuresPath = null)
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
            _ => Task.FromResult(check?.Invoke() ?? readiness ?? Ready), _ => Task.FromResult(Ready), failuresPath);
        return (model, background, store);
    }

    [WpfFact]
    public async Task AddingAFolder_DescribesItsPhotos_IncludingSubfolders_ButNotVideos()
    {
        var (model, background, store) = await StartAsync(enabled: true);
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.WhenIdleAsync();
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
            await background.WhenIdleAsync();
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
            await background.WhenIdleAsync();
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
            await background.WhenIdleAsync();
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
            await background.WhenIdleAsync();
            Assert.Empty(_described);
            Assert.DoesNotContain(_announced, s => s.Contains("Copilot+"));
            Assert.Null(model.BackgroundProgress);

            Assert.DoesNotContain(_announced, s => s.StartsWith("Describing"));

            background.Enabled = true;
            await background.WhenIdleAsync();
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
            Assert.True(model.IsBusy);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Null(model.BackgroundProgress);
            Assert.False(model.IsBusy);
        }
    }

    [WpfFact]
    public async Task RemovingTheFolderMidRun_SkipsTheRest()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            await model.RemoveFolderAsync(_added.Root);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Single(_described);
            Assert.DoesNotContain(_announced, s => s.StartsWith("Finished describing"));
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task Stop_EmptiesTheQueue_SaysHowManyWereLeft_AndKeepsTheSetting()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            Assert.True(background.IsRunning);
            background.Stop();
            Assert.Contains("Stopped describing in the background. 1 photo not described; it carries on the next time My Photo Library Described starts.", _announced);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Single(_described);
            Assert.Null(model.BackgroundProgress);
            Assert.True(background.Enabled);
        }
    }

    [WpfFact]
    public async Task TurningItOffMidRun_SaysItStopped()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            background.Enabled = false;
            Assert.Contains("Background describing off. Stopped with 1 photo not described.", _announced);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Single(_described);
        }
    }

    [WpfFact]
    public async Task AddingTheSameFolderTwice_DoesntDescribeAnythingTwice()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            await model.AddFolderAsync(_added.Root);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Equal(2, _described.Count);
            Assert.Equal(_described.Count, _described.Distinct().Count());
        }
    }

    [WpfFact]
    public async Task ASecondFolderAddedMidRun_IsDescribedToo_AndEachIsAnnounced()
    {
        using var second = new TestLibrary();
        second.AddJpeg("c.jpg");
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async _ => { await release.Task; return "Done"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            await model.AddFolderAsync(second.Root);
            Assert.Equal("Describing photos in the background, 1 of 3", model.BackgroundProgress);
            release.SetResult();
            await background.WhenIdleAsync();
            Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], _described.Order());
            Assert.Equal(2, _announced.Count(s => s.StartsWith("Finished describing")));
        }
    }

    [WpfFact]
    public async Task ArrowingComesFirst_BackgroundWaitsWhileForegroundIsBusy()
    {
        var (model, background, _) = await StartAsync(enabled: true);
        using (model) using (background)
        {
            var foreground = true;
            background.ForegroundBusy = () => foreground;
            await model.AddFolderAsync(_added.Root);
            await Task.Delay(400);
            Assert.Empty(_described);
            foreground = false;
            await background.WhenIdleAsync();
            Assert.Equal(2, _described.Count);
        }
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(10);
    }

    [WpfFact]
    public async Task TheSetting_IsSaved()
    {
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            background.Enabled = true;
            Assert.True(Settings.Load(_lib.PathOf(@"data\Settings.json")).DescribeInBackground);
            Assert.Contains("Photos in the library will be described in the background", _announced);
        }
    }

    [WpfFact]
    public async Task TurningItOn_DescribesTheLibrarysPhotosNotDescribedYet_AndSaysWhenItsDone()
    {
        var (model, background, store) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await background.WhenIdleAsync();
            Assert.Empty(_described);
            // One already described is skipped.
            store.Put(model.AllItems.Single(i => i.Name == "a.jpg"), "accessible", "Already.");
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Equal(["b.jpg", "home.jpg"], _described.Order());
            Assert.Contains("Describing 2 photos in the background", _announced);
            Assert.Contains("Finished describing the library's photos", _announced);
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task AtAStart_ItCarriesOnWithWhatsLeft_Quietly()
    {
        var (model, background, store) = await StartAsync(enabled: true);
        using (model) using (background)
        {
            store.Put(model.AllItems.Single(i => i.Name == "home.jpg"), "accessible", "Done last time.");
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Empty(_described);
            await model.AddFolderAsync(_added.Root);
            await background.WhenIdleAsync();
            _described.Clear();
            _announced.Clear();
            // A new start: nothing left, so nothing described and nothing said.
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Empty(_described);
            Assert.Empty(_announced);
        }
    }

    [WpfFact]
    public async Task AtAStart_TheStatusBarShowsXOfY_AndNothingIsSaid()
    {
        var release = new TaskCompletionSource();
        var (model, background, _) = await StartAsync(enabled: true, describe: async item => { await release.Task; return $"About {item.Name}"; });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => model.BackgroundProgress is not null);
            release.SetResult();
            await background.WhenIdleAsync();
            _announced.Clear();
            var second = new TaskCompletionSource();
            var (model2, background2, _) = await StartAsync(enabled: true, describe: async item => { await second.Task; return "x"; });
            using (model2) using (background2)
            {
                var start = background2.DescribeLibraryAsync();
                await WaitUntil(() => model2.BackgroundProgress is not null);
                Assert.StartsWith("Describing photos in the background, 1 of ", model2.BackgroundProgress);
                Assert.Empty(_announced);
                second.SetResult();
                await start;
                await background2.WhenIdleAsync();
            }
        }
    }

    [WpfFact]
    public async Task WithoutWindowsAI_AStartDescribesNothing()
    {
        var (model, background, _) = await StartAsync(enabled: true, new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."));
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Empty(_described);
            Assert.Empty(_announced);
        }
    }

    [WpfFact]
    public async Task TurningItOffAndOnMidPhoto_CountsRight_AndSaysFinishedOnlyAtTheEnd()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        var (model, background, _) = await StartAsync(enabled: true, describe: async item =>
        {
            if (first) { first = false; await gate.Task; }
            return $"About {item.Name}";
        });
        using (model) using (background)
        {
            await model.AddFolderAsync(_added.Root);
            await WaitUntil(() => _described.Count == 1);
            background.Enabled = false;
            background.Enabled = true;
            await WaitUntil(() => model.BackgroundProgress is not null);
            gate.SetResult();
            await background.WhenIdleAsync();
            // Every photo described once, and "finished" said once, at the end.
            Assert.Equal(_described.Distinct().Count(), _described.Count);
            Assert.Equal(["a.jpg", "b.jpg", "home.jpg"], _described.Order());
            Assert.Single(_announced, s => s == "Finished describing the library's photos");
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task AFolderAddedDuringALibraryRun_GoesFirst()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        var (model, background, _) = await StartAsync(enabled: false, describe: async item =>
        {
            if (first) { first = false; await gate.Task; }
            return $"About {item.Name}";
        });
        using (model) using (background)
        {
            _lib.AddJpeg("older1.jpg");
            _lib.AddJpeg("older2.jpg");
            await model.RescanAsync();
            background.Enabled = true;
            await WaitUntil(() => _described.Count == 1);
            await model.AddFolderAsync(_added.Root);
            gate.SetResult();
            await background.WhenIdleAsync();
            // After the photo already being described, the added folder's photos come before the rest of the library.
            Assert.Equal(["a.jpg", "b.jpg"], _described.Skip(1).Take(2).Order());
            Assert.Contains(_announced, s => s.StartsWith("Finished describing the photos in"));
        }
    }

    [WpfTheory]
    [InlineData(6, "Background describing stopped: 5 photos in a row couldn't be described, so 1 photo wasn't tried. ")]
    [InlineData(5, "Background describing stopped: 5 photos in a row couldn't be described. ")]
    public async Task TheStopSaysHowManyWerentTried_InWords(int photos, string said)
    {
        // #158, as the Mac's #159: not "so 1 photo weren't tried" or "so 0 photos weren't tried".
        // home.jpg is there already.
        for (var i = 1; i < photos; i++) _lib.AddJpeg($"broken{i}.jpg");
        var (model, background, _) = await StartAsync(enabled: false,
            describe: _ => throw new DescriptionException("decode_failed", "The picture couldn't be read."));
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Single(_announced, s => s.StartsWith(said, StringComparison.Ordinal));
        }
    }

    [WpfFact]
    public async Task PhotosTheContentFilterDeclines_DontStopTheRun_AndArentTriedAgainAfterARestart()
    {
        // Kelly, 13 Oct: the newest photos included some Windows' filter declines and some it couldn't describe, so every
        // start stopped after the same five and never reached the rest.
        for (var i = 0; i < 8; i++) _lib.AddJpeg($"filtered{i}.jpg");
        _lib.AddJpeg("fine.jpg");
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        Task<string> Describe(MediaItem item) => item.Name.StartsWith("filtered", StringComparison.Ordinal)
            ? throw new DescriptionException("content_filtered", "Windows' content filter declined the text in this picture.")
            : Task.FromResult($"About {item.Name}");
        var (model, background, store) = await StartAsync(enabled: false, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Contains("fine.jpg", _described);
            Assert.DoesNotContain(_announced, s => s.StartsWith("Background describing stopped:"));
        }
        // After a restart, the declined photos aren't tried again.
        _described.Clear();
        (model, background, store) = await StartAsync(enabled: true, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.DoesNotContain(_described, n => n.StartsWith("filtered", StringComparison.Ordinal));
        }
        // A new version of the app tries them once more.
        File.WriteAllText(failures, File.ReadAllText(failures).Replace(Updates.AppVersion, "0.0.1"));
        _described.Clear();
        (model, background, store) = await StartAsync(enabled: true, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Contains(_described, n => n.StartsWith("filtered", StringComparison.Ordinal));
        }
    }

    /// <summary>Photos whose names start with <paramref name="prefix"/> made the newest, so they're first in the queue.</summary>
    private void MakeNewest(string prefix)
    {
        foreach (var f in Directory.GetFiles(_lib.Root, "*.jpg"))
            File.SetLastWriteTime(f, Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal) ? DateTime.Now.AddMinutes(-1) : DateTime.Now.AddYears(-1));
    }

    [WpfFact]
    public async Task KellysLibrary_TwoFilteredThenThreeErrorsAtTheFront_IsDescribedPastThem_AndTheyreNotTriedAgain()
    {
        // Kelly, 13 Oct: every start stopped on the same five newest photos, so 5,794 were never tried.
        _lib.AddJpeg("bad-filtered1.jpg");
        _lib.AddJpeg("bad-filtered2.jpg");
        _lib.AddJpeg("bad-error1.jpg");
        _lib.AddJpeg("bad-error2.jpg");
        _lib.AddJpeg("bad-error3.jpg");
        for (var i = 0; i < 3; i++) _lib.AddJpeg($"good{i}.jpg");
        MakeNewest("bad-");
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        Task<string> Describe(MediaItem item) => item.Name.StartsWith("bad-filtered", StringComparison.Ordinal)
            ? throw new DescriptionException("content_filtered", "Windows' content filter declined the text in this picture.")
            : item.Name.StartsWith("bad-error", StringComparison.Ordinal)
                ? throw new DescriptionException("internal_error", "Windows couldn't describe this picture (InternalError).")
                : Task.FromResult($"About {item.Name}");
        var (model, background, _) = await StartAsync(enabled: false, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Contains("good0.jpg", _described);
            Assert.DoesNotContain(_announced, s => s.StartsWith("Background describing stopped:"));
        }
        _described.Clear();
        (model, background, _) = await StartAsync(enabled: true, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            // A photo described after them proved describing works, so all five are remembered.
            Assert.DoesNotContain(_described, n => n.StartsWith("bad-", StringComparison.Ordinal));
        }
    }

    [WpfFact]
    public async Task ARunThatStopsAfterFailuresInARow_DoesntKeepThem_SoTheyreTriedAgainOnceThingsWork()
    {
        // A problem with every photo (a missing codec, say): once it's fixed, nothing is left skipped.
        for (var i = 0; i < 7; i++) _lib.AddJpeg($"photo{i}.jpg");
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        var (model, background, _) = await StartAsync(enabled: false, failuresPath: failures,
            describe: _ => throw new DescriptionException("decode_failed", "The picture couldn't be read."));
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Single(_announced, s => s.StartsWith("Background describing stopped:"));
        }
        var failed = _described.ToList();
        _described.Clear();
        (model, background, _) = await StartAsync(enabled: true, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Subset(_described.ToHashSet(), failed.ToHashSet());
        }
    }

    [WpfTheory]
    [InlineData("{\"Version\":\"VERSION\"}")]
    [InlineData("{\"Version\":\"VERSION\",\"Keys\":null}")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task AFailuresFileItCantRead_IsIgnored_AndEverythingIsDescribed(string contents)
    {
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        Directory.CreateDirectory(Path.GetDirectoryName(failures)!);
        File.WriteAllText(failures, contents.Replace("VERSION", Updates.AppVersion));
        var (model, background, _) = await StartAsync(enabled: true, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Contains("home.jpg", _described);
        }
    }

    [WpfFact]
    public async Task TooMuchText_DoesntStopTheRun_ButATimeoutIsntRemembered()
    {
        for (var i = 0; i < 6; i++) _lib.AddJpeg($"text{i}.jpg");
        _lib.AddJpeg("slow.jpg");
        _lib.AddJpeg("ok.jpg");
        MakeNewest("text");
        File.SetLastWriteTime(_lib.PathOf("slow.jpg"), DateTime.Now.AddMinutes(-2));
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        var slow = true;
        Task<string> Describe(MediaItem item) => item.Name.StartsWith("text", StringComparison.Ordinal)
            ? throw new DescriptionException("too_much_text", "There's too much text in this picture to describe.")
            : item.Name == "slow.jpg" && slow ? throw new DescriptionException("timeout", "Windows took too long. Try again.")
            : Task.FromResult("Fine");
        var (model, background, _) = await StartAsync(enabled: false, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.DoesNotContain(_announced, s => s.StartsWith("Background describing stopped:"));
            Assert.Contains("ok.jpg", _described);
        }
        // The timed-out photo is tried again at the next start; the ones with too much text aren't.
        slow = false;
        _described.Clear();
        (model, background, _) = await StartAsync(enabled: true, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.Contains("slow.jpg", _described);
            Assert.DoesNotContain(_described, n => n.StartsWith("text", StringComparison.Ordinal));
        }
    }

    [WpfFact]
    public async Task TurningItOnAgain_TriesThePhotosThatCouldntBeDescribed()
    {
        _lib.AddJpeg("declined.jpg");
        var failures = _lib.PathOf(@"data\BackgroundFailures.json");
        var declines = true;
        Task<string> Describe(MediaItem item) => declines && item.Name == "declined.jpg"
            ? throw new DescriptionException("content_filtered", "No.")
            : Task.FromResult("Fine");
        var (model, background, _) = await StartAsync(enabled: false, describe: Describe, failuresPath: failures);
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            declines = false;
            _described.Clear();
            background.Enabled = false;
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Contains("declined.jpg", _described);
        }
    }

    [WpfFact]
    public async Task ManyPhotosFailingInARow_StopsTheRun_AndSaysWhyOnce()
    {
        for (var i = 0; i < 8; i++) _lib.AddJpeg($"heic{i}.jpg");
        var (model, background, _) = await StartAsync(enabled: false,
            describe: _ => throw new DescriptionException("decode_failed", "The picture couldn't be read."));
        using (model) using (background)
        {
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Equal(BackgroundDescriber.FailuresInARow, _described.Count);
            Assert.Single(_announced, s => s.StartsWith("Background describing stopped:") && s.Contains("The picture couldn't be read."));
            Assert.Null(model.BackgroundProgress);
            // The ones that failed aren't tried again this session (and, once describing has worked, not after a restart
            // either): the next run starts with the photos not tried.
            var tried = _described.ToList();
            _described.Clear();
            await background.DescribeLibraryAsync();
            await background.WhenIdleAsync();
            Assert.NotEmpty(_described);
            Assert.Empty(_described.Intersect(tried));
        }
    }

    [WpfFact]
    public async Task OnEnergySaver_ItWaits_AndSaysSoInTheStatusBar()
    {
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            var saving = true;
            background.MayRun = () => !saving;
            background.Enabled = true;
            await WaitUntil(() => model.BackgroundProgress == "Background describing paused on energy saver");
            Assert.Empty(_described);
            saving = false;
            await background.WhenIdleAsync();
            Assert.Equal(["home.jpg"], _described);
        }
    }

    [WpfFact]
    public async Task NewestPhotosAreDescribedFirst()
    {
        var older = _lib.AddJpeg("older.jpg");
        var newer = _lib.AddJpeg("newer.jpg");
        File.SetLastWriteTime(older, new DateTime(2020, 1, 1));
        File.SetLastWriteTime(newer, new DateTime(2025, 1, 1));
        File.SetLastWriteTime(_lib.PathOf("home.jpg"), new DateTime(2022, 1, 1));
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            // Before details are read, "newest" is the file's date.
            foreach (var item in model.AllItems) item.Metadata = null;
            background.Enabled = true;
            await background.WhenIdleAsync();
            Assert.Equal(["newer.jpg", "home.jpg", "older.jpg"], _described);
        }
    }

    [WpfFact]
    public async Task StopWhilePausedOnEnergySaver_ClearsTheStatusBar_AndEndsTheRun()
    {
        var (model, background, _) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            background.MayRun = () => false;
            background.Enabled = true;
            await WaitUntil(() => model.BackgroundProgress == "Background describing paused on energy saver");
            background.Stop();
            await background.WhenIdleAsync();
            Assert.Null(model.BackgroundProgress);
            Assert.False(background.IsRunning);
            Assert.Empty(_described);
        }
    }

    [WpfFact]
    public async Task AddingAFolderThatsAllInTheLibrarysBacklog_StillSaysTheLibraryIsFinished()
    {
        var (model, background, store) = await StartAsync(enabled: false);
        using (model) using (background)
        {
            store.Put(model.AllItems.Single(i => i.Name == "home.jpg"), "accessible", "Done.");
            var inside = _lib.AddJpeg(@"Trip\x.jpg");
            _lib.AddJpeg(@"Trip\y.jpg");
            await model.RescanAsync();
            background.MayRun = () => false;
            background.Enabled = true;
            await WaitUntil(() => model.BackgroundProgress is not null);
            // The folder is inside the library, so its photos move ahead of the backlog: the whole backlog.
            await model.AddFolderAsync(Path.GetDirectoryName(inside)!);
            background.MayRun = () => true;
            await background.WhenIdleAsync();
            Assert.Equal(["x.jpg", "y.jpg"], _described.Order());
            Assert.Single(_announced, s => s == "Finished describing the library's photos");
        }
    }

    [WpfFact]
    public async Task AtAStart_WindowsAINotAnsweringAtFirst_IsAskedAgain_AndTheLibraryIsDescribed()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => ++asked < 3 ? new AIReadiness(false, "Error", "The helper was slow this time.") : Ready);
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
            background.Resume();
            await WaitUntil(() => _described.Count == 1);
            await background.WhenIdleAsync();
            Assert.Equal(3, asked);
            Assert.Equal(["home.jpg"], _described);
            Assert.Empty(_announced);
            Assert.Contains(File.ReadAllLines(Log.TodaysFile), l => l.Contains("[background] Not started: Windows AI is Error. The helper was slow this time."));
        }
    }

    [WpfFact]
    public async Task AtAStart_WindowsAINotAnswering_TheStatusBarSaysItsWaiting_WithoutABusyIndicator_UntilTurnedOff()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => { asked++; return new AIReadiness(false, "Error", "The helper didn't answer."); });
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
            background.Resume();
            // Said from the first try that fails, and asked again meanwhile.
            await WaitUntil(() => asked >= 3);
            Assert.Equal(BackgroundDescriber.WaitingText, model.BackgroundProgress);
            // Nothing's going on, so no busy indicator.
            Assert.False(model.IsBusy);
            Assert.Empty(_described);
            background.Enabled = false;
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task AtAStart_WindowsAINotAvailable_IsntAskedAgain()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => { asked++; return new AIReadiness(false, "NotSupportedOnCurrentSystem", "Needs a Copilot+ PC."); });
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
            background.Resume();
            await background.WhenIdleAsync();
            Assert.Equal(1, asked);
            Assert.Equal("Background describing can't run. Needs a Copilot+ PC.", model.BackgroundProgress);
            Assert.False(model.IsBusy);
            Assert.Empty(_announced);
        }
    }

    [WpfFact]
    public async Task AtAStart_TurnedOffWhileWaitingToAskAgain_StopsAsking()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => { asked++; return new AIReadiness(false, "Error", "The helper didn't answer."); });
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)];
            background.Resume();
            await WaitUntil(() => asked == 1);
            background.Enabled = false;
            await background.WhenIdleAsync();
            Assert.Equal(1, asked);
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task AtAStart_TheModelNotReady_IsGotReady_SayingSo_AndDescribingStarts()
    {
        var prepared = 0;
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var settings = new Settings { DescribeInBackground = true };
        using var model = new LibraryModel(settings, _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1)) { Descriptions = store };
        await model.StartAsync();
        var release = new TaskCompletionSource<AIReadiness>();
        using var background = new BackgroundDescriber(model, store, (item, _, _) => { _described.Add(item.Name); return Task.FromResult<Described>("x"); },
            _ => Task.FromResult(new AIReadiness(true, "NotReady", null)), _ => { prepared++; return release.Task; });
        background.ResumeRetries = [TimeSpan.FromMilliseconds(10)];
        background.Resume();
        await WaitUntil(() => prepared == 1);
        // Nothing for the person to do: the status bar says what's happening.
        Assert.Equal("Getting Windows' image description model ready for background describing", model.BackgroundProgress);
        release.SetResult(Ready);
        await WaitUntil(() => _described.Count == 1);
        await background.WhenIdleAsync();
        Assert.Equal(["home.jpg"], _described);
        Assert.Null(model.BackgroundProgress);
        Assert.Empty(_announced);
    }

    [WpfFact]
    public async Task WaitingForWindowsAI_StartsByItself_WhenADescriptionWorks()
    {
        var working = false;
        var checks = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => { checks++; return working ? Ready : new AIReadiness(false, "Error", "No answer."); });
        using (model) using (background)
        {
            background.ResumeRetries = [];
            background.Resume();
            await WaitUntil(() => model.BackgroundProgress is not null);
            await background.WhenIdleAsync();
            Assert.Equal(BackgroundDescriber.WaitingText, model.BackgroundProgress);
            Assert.DoesNotContain("off and on", model.BackgroundProgress);

            // Describe Photo works: background describing starts, and the message goes.
            working = true;
            background.WindowsAIWorked();
            await WaitUntil(() => _described.Count == 1);
            await background.WhenIdleAsync();
            Assert.Equal(["home.jpg"], _described);
            Assert.Null(model.BackgroundProgress);

            // A later description doesn't start it again: Windows AI isn't even asked.
            var asked = checks;
            background.WindowsAIWorked();
            await background.WhenIdleAsync();
            Assert.Equal(asked, checks);
        }
    }

    [WpfFact]
    public async Task StopWhileWaitingForWindowsAI_MeansStop_EvenWhenADescriptionThenWorks()
    {
        var checks = 0;
        var (model, background, _) = await StartAsync(enabled: true, check: () => { checks++; return new AIReadiness(false, "Error", "No answer."); });
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10)];
            background.ResumePoll = TimeSpan.FromMilliseconds(10);
            background.Resume();
            await WaitUntil(() => model.BackgroundProgress == BackgroundDescriber.WaitingText);
            background.Stop();
            Assert.Null(model.BackgroundProgress);
            var asked = checks;
            background.WindowsAIWorked();
            await Task.Delay(200);
            Assert.Equal(asked, checks);
            Assert.Empty(_described);
        }
    }

    [WpfFact]
    public async Task WaitingForWindowsAI_KeepsAsking_AndStartsWhenItAnswers()
    {
        var checks = 0;
        var (model, background, _) = await StartAsync(enabled: true, check: () => ++checks < 6 ? new AIReadiness(false, "Error", "No answer.") : Ready);
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10)];
            background.ResumePoll = TimeSpan.FromMilliseconds(10);
            background.Resume();
            // Past the quick retries, it goes on asking, saying it's waiting meanwhile.
            await WaitUntil(() => _described.Count == 1);
            Assert.True(checks >= 6);
            Assert.Equal(["home.jpg"], _described);
        }
    }

    [Fact]
    public async Task AFinishedDescription_IsAnnounced_OnlyWhenItWorked_AndAListenerCantMakeItFail()
    {
        var raised = 0;
        void Count() { raised++; throw new InvalidOperationException("A listener that fails"); }
        WindowsAI.DescribedOne += Count;
        try
        {
            var stats = new DescribeStats();
            Assert.Equal("Fine", await WindowsAI.CountedAsync(stats, () => Task.FromResult(("Fine", TimeSpan.FromSeconds(1))), default, default));
            Assert.Equal(1, raised);
            Assert.Empty(stats.RecentFailures());
            await Assert.ThrowsAsync<DescriptionException>(() => WindowsAI.CountedAsync(stats,
                () => throw new DescriptionException("internal_error", "No."), default, default));
            Assert.Equal(1, raised);
        }
        finally
        {
            WindowsAI.DescribedOne -= Count;
        }
    }

    [WpfFact]
    public async Task AtAStart_TheCheckThrowing_IsAskedAgain()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => ++asked < 2 ? throw new IOException("The helper couldn't be started.") : Ready);
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(10)];
            background.Resume();
            await WaitUntil(() => _described.Count == 1);
            await background.WhenIdleAsync();
            Assert.Equal(2, asked);
            Assert.Equal(["home.jpg"], _described);
        }
    }

    [WpfFact]
    public async Task AtAStart_StopWhileWaitingToAskAgain_StopsAsking_UntilTheNextStart()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => ++asked < 2 ? new AIReadiness(false, "Error", "No answer.") : Ready);
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(200)];
            background.Resume();
            await WaitUntil(() => asked == 1);
            background.Stop();
            await background.WhenIdleAsync();
            Assert.Equal(1, asked);
            Assert.Empty(_described);
            Assert.Null(model.BackgroundProgress);
        }
    }

    [WpfFact]
    public async Task AtAStart_TurnedOffAndOnWhileWaitingToAskAgain_DescribesOnce()
    {
        var asked = 0;
        var (model, background, _) = await StartAsync(enabled: true,
            check: () => ++asked < 2 ? new AIReadiness(false, "Error", "No answer.") : Ready);
        using (model) using (background)
        {
            background.ResumeRetries = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)];
            background.Resume();
            await WaitUntil(() => asked == 1);
            background.Enabled = false;
            background.Enabled = true;
            await background.WhenIdleAsync();
            await Task.Delay(500);
            await background.WhenIdleAsync();
            Assert.Equal(["home.jpg"], _described);
            // Asked when turned on, twice (the check, then the library); not again from the start.
            Assert.Equal(3, asked);
            Assert.Null(model.BackgroundProgress);
        }
    }
}
