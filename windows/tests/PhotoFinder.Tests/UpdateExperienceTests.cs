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

    public void Dispose() => _lib.Dispose();

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
}
