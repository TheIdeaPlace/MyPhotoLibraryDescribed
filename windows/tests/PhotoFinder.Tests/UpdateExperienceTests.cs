using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>The apps' shared update experience (The-Idea-Place-Projects#12, #85): Help always says what's running or
/// what's available, and the first launch after an update says so.</summary>
[Collection(SharedState.Name)]
public sealed class UpdateExperienceTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose()
    {
        Updates.InstalledVersion = () => Updates.PackageVersion;
        Updates.SelfUpdating = () => Updates.UpdatesItself;
        Updates.QuietCheck = Updates.CheckQuietlyAsync;
        MainWindow.OpenUrl = url => FileActions.Open(url);
        _lib.Dispose();
    }

    private LibraryModel NewModel(Settings? settings = null) =>
        new(settings ?? new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));

    [Fact]
    public void HelpsItem_SaysWhatsRunning_OrWhatsAvailable()
    {
        Assert.Equal("Check for _Updates (running 0.2.0)…", Updates.MenuTitle("0.2.0.310", false, null));
        Assert.Equal("_Update to 0.3.0 available…", Updates.MenuTitle("0.2.0.310", true, "0.3.0.400"));
        // Only the build differs (test builds): the full numbers, so it doesn't read "Update to 0.2.0" while running 0.2.0.
        Assert.Equal("_Update to 0.2.0.320 available…", Updates.MenuTitle("0.2.0.310", true, "0.2.0.320"));
        Assert.Equal("_Update Available…", Updates.MenuTitle("0.2.0.310", true, null));
    }

    [Theory]
    [InlineData(null, "0.2.0.310", false)]          // first run
    [InlineData("0.2.0.310", "0.2.0.310", false)]   // same version
    [InlineData("0.3.0.400", "0.2.0.310", false)]   // went back to an older one
    [InlineData("0.2.0.310", "0.2.0.320", true)]
    [InlineData("0.1.1.290", "0.2.0.310", true)]
    [InlineData("not a version", "0.2.0.310", false)]
    public void TheUpdateInstalledNote_IsOnlyForANewerVersionThanLastRan(string? last, string current, bool shown) =>
        Assert.Equal(shown, Updates.JustUpdated(last, current));

    [Fact]
    public void TheAvailableVersion_IsReadFromTheAppInstallerFile()
    {
        Assert.Equal("0.3.0.400", Updates.VersionIn("""
            <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="0.3.0.400" Uri="https://example.com/PhotoFinder.appinstaller">
              <MainBundle Name="TheIdeaPlace.PhotoFinder" Publisher="CN=x" Version="0.3.0.400" Uri="https://example.com/a.msixbundle" />
            </AppInstaller>
            """));
        Assert.Null(Updates.VersionIn("not xml"));
        // The bundle's version, and only one that reads as a version.
        Assert.Equal("0.3.0.400", Updates.VersionIn("""<AppInstaller Version="9.9.9.9"><MainBundle Version="0.3.0.400" /></AppInstaller>"""));
        Assert.Null(Updates.VersionIn("""<AppInstaller><MainBundle Version="" /></AppInstaller>"""));
        Assert.True(Updates.IsNewer("0.3.0.400", "0.2.0.310"));
        Assert.False(Updates.IsNewer("0.2.0.310", "0.2.0.310"));
        Assert.False(Updates.IsNewer("junk", "0.2.0.310"));
    }

    [Fact]
    public void TheRepository_IsInOnePlace()
    {
        Assert.Equal("https://github.com/kellylford/PhotoFinder/issues/new", AppInfo.IssuesUrl);
        Assert.Equal("https://github.com/kellylford/PhotoFinder/releases/tag/windows-v0.2.0", AppInfo.ReleaseNotesUrl("0.2.0"));
        Assert.Equal("0.2.0", Updates.ShortVersion("0.2.0.310"));
    }

    [WpfFact]
    public async Task TheMenuItem_SaysWhatsRunning_AndARunWithoutAnInstallSaysNothingAboutUpdates()
    {
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        var window = new MainWindow(model) { ShowActivated = false };
        var original = QuestionDialog.AskSafely;
        try
        {
            QuestionDialog.AskSafely = (_, _, _, _, _, _) => throw new InvalidOperationException("Asked without an update");
            window.Show();
            await Task.Delay(200);
            Assert.StartsWith("Check for _Updates (running ", window.UpdatesItem.Header as string);
            // Not installed (a test run has no package): nothing recorded, nothing shown.
            window.SayIfJustUpdated();
            Assert.Null(model.Settings.LastRunVersion);
        }
        finally
        {
            QuestionDialog.AskSafely = original;
            window.Close();
            model.Dispose();
        }
    }

    [WpfFact]
    public async Task TheQuietCheck_IsRememberedWhenItFindsOut_AndTriedAgainWhenItCouldnt()
    {
        Updates.InstalledVersion = () => "0.2.0.310";
        // Not self-updating while the window opens, so its own quiet check (five seconds later) stays out of the count.
        Updates.SelfUpdating = () => false;
        var answer = (Updates.State.Failed, (string?)null);
        var checks = 0;
        Updates.QuietCheck = () => { checks++; return Task.FromResult(answer); };
        var model = NewModel();
        var window = new MainWindow(model) { ShowActivated = false };
        try
        {
            window.Show();
            Updates.SelfUpdating = () => true;
            // Offline: nothing recorded, so the next launch tries again.
            await window.LookForUpdatesQuietlyAsync(TimeSpan.Zero);
            Assert.Null(model.Settings.LastUpdateCheck);
            answer = (Updates.State.Available, "0.3.0.400");
            await window.LookForUpdatesQuietlyAsync(TimeSpan.Zero);
            Assert.Equal("_Update to 0.3.0 available…", window.UpdatesItem.Header);
            Assert.Equal("0.3.0.400", Settings.Load(_lib.PathOf(@"data\Settings.json")).AvailableUpdate);
            // Within the day, no check; and a new window still says what was found.
            await window.LookForUpdatesQuietlyAsync(TimeSpan.Zero);
            Assert.Equal(2, checks);
        }
        finally
        {
            window.Close();
            model.Dispose();
        }
        var again = NewModel(Settings.Load(_lib.PathOf(@"data\Settings.json")));
        var second = new MainWindow(again) { ShowActivated = false };
        try
        {
            second.Show();
            await Task.Delay(200);
            Assert.Equal("_Update to 0.3.0 available…", second.UpdatesItem.Header);
        }
        finally
        {
            second.Close();
            again.Dispose();
        }
    }

    [WpfFact]
    public async Task TheFirstLaunchAfterAnUpdate_SaysSoOnce_AndSeeWhatsNewOpensTheReleaseNotes()
    {
        Updates.InstalledVersion = () => "0.3.0.400";
        var opened = new List<string>();
        MainWindow.OpenUrl = opened.Add;
        var asked = new List<string>();
        var original = QuestionDialog.AskSafely;
        var model = NewModel(new Settings { LastRunVersion = "0.2.0.310", AvailableUpdate = "0.3.0.400" });
        var window = new MainWindow(model) { ShowActivated = false };
        try
        {
            QuestionDialog.AskSafely = (_, title, question, _, yes, keep) => { asked.Add($"{title}|{question}|{yes}|{keep}"); return true; };
            window.Show();
            await Task.Delay(300);
            asked.Clear();
            opened.Clear();
            model.Settings.LastRunVersion = "0.2.0.310";
            Assert.True(window.SayIfJustUpdated());
            Assert.Equal(["Photo Finder Update Installed|Photo Finder is now version 0.3.0.|_See what's new|OK"], asked);
            Assert.Equal(["https://github.com/kellylford/PhotoFinder/releases/tag/windows-v0.3.0"], opened);
            Assert.Equal("0.3.0.400", Settings.Load(_lib.PathOf(@"data\Settings.json")).LastRunVersion);
            // The update that was waiting is the one now running.
            Assert.Null(model.Settings.AvailableUpdate);
            Assert.StartsWith("Check for _Updates (running ", window.UpdatesItem.Header as string);
            // Once only.
            Assert.False(window.SayIfJustUpdated());

            // A newer build of the same version: the releases list, since it has no notes of its own.
            opened.Clear();
            Updates.InstalledVersion = () => "0.3.0.410";
            Assert.True(window.SayIfJustUpdated());
            Assert.Equal(["https://github.com/kellylford/PhotoFinder/releases"], opened);
        }
        finally
        {
            QuestionDialog.AskSafely = original;
            window.Close();
            model.Dispose();
        }
    }
}
