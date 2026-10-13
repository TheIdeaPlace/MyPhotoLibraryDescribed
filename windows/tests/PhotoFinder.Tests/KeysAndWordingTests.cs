using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>The keys and wording both apps share (#119, #105): Describe Photo on Ctrl+D, Ctrl+Shift+D to the description
/// and back, Redescribe on Ctrl+R, the Help menu's user guide and privacy policy, and the bug report's log sentence.</summary>
[Collection(SharedState.Name)]
public sealed class KeysAndWordingTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public KeysAndWordingTests() => _lib.AddJpeg("a.jpg");

    public void Dispose() => _lib.Dispose();

    private async Task<(LibraryModel Model, MainWindow Window)> OpenAsync()
    {
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
            { Descriptions = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json")) };
        await model.StartAsync();
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        return (model, window);
    }

    private static bool Has(RoutedUICommand command, Key key, ModifierKeys modifiers) =>
        command.InputGestures.OfType<KeyGesture>().Any(g => g.Key == key && g.Modifiers == modifiers);

    [Fact]
    public void TheShortcuts_MatchTheMac()
    {
        Assert.True(Has(AppCommands.DescribePhoto, Key.D, ModifierKeys.Control));
        Assert.True(Has(AppCommands.GoToDescription, Key.D, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(Has(AppCommands.Redescribe, Key.R, ModifierKeys.Control));
        Assert.Equal("Redescri_be", AppCommands.Redescribe.Text);
        // Refresh stays F5, and nothing else has Ctrl+D, Ctrl+Shift+D or Ctrl+R.
        Assert.True(Has(AppCommands.Refresh, Key.F5, ModifierKeys.None));
        var all = typeof(AppCommands).GetFields().Select(f => f.GetValue(null)).OfType<RoutedUICommand>().ToList();
        foreach (var (key, mods) in new[] { (Key.D, ModifierKeys.Control), (Key.D, ModifierKeys.Control | ModifierKeys.Shift), (Key.R, ModifierKeys.Control) })
            Assert.Single(all, c => Has(c, key, mods));
    }

    [WpfFact]
    public async Task GoToDescription_ShowsThePanel_GoesToItsBox_AndBack()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                window.ShowDescriptionPanel(false, announce: false);
                window.FileList.Focus();
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.IsKeyboardFocusWithin);
                Assert.True(window.DescriptionPanel.IsVisible);
                Assert.True(window.DescriptionPanel.IsKeyboardFocusWithin);

                window.GoToDescriptionOrBack();
                await WaitFor(() => !window.DescriptionPanel.IsKeyboardFocusWithin);
                Assert.False(window.DescriptionPanel.IsKeyboardFocusWithin);
                Assert.True(window.FileList.IsKeyboardFocusWithin);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task Help_OpensTheUserGuideAndPrivacyPolicy_OnGitHub()
    {
        var (model, window) = await OpenAsync();
        var opened = new List<string>();
        var original = MainWindow.OpenUrl;
        MainWindow.OpenUrl = opened.Add;
        using (model)
        {
            try
            {
                AppCommands.UserGuide.Execute(null, window);
                AppCommands.PrivacyPolicy.Execute(null, window);
                Assert.Equal([$"https://github.com/{AppInfo.Repository}/blob/main/docs/user-guide.md",
                    $"https://github.com/{AppInfo.Repository}/blob/main/docs/PRIVACY.md"], opened);
                Assert.Equal($"{AppInfo.Name} User _Guide", AppCommands.UserGuide.Text);
            }
            finally
            {
                MainWindow.OpenUrl = original;
                window.Close();
            }
        }
    }

    [Fact]
    public void TheModes_AreNamedWithMode()
    {
        Assert.Equal($"{AppInfo.Name} is in Read-Only Mode, so photos can't be changed. To change them, turn off Read-Only Mode in the File menu.",
            PhotoChanges.ReadOnlyMessage);
    }

    [Theory]
    [InlineData("A red mug on a wooden surface beside a sign reading OPEN.")]
    [InlineData("A red mug on a kitchen counter next to an OPEN sign.")]
    public async Task CheckAIDescription_TakesASurfaceOrCounter_ForTheTable(string description)
    {
        var result = await AIDescriptionCheck.RunAsync((_, _, _) => Task.FromResult(description), "accessible", null, default);
        Assert.Empty(result.Missing);
    }

    [WpfFact]
    public async Task GoToDescription_FromTheGallery_GoesBackToTheGallery_AndOnAFolder_ReachesWhatThePanelSays()
    {
        Directory.CreateDirectory(_lib.PathOf("Trip"));
        _lib.AddJpeg(@"Trip\b.jpg");
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                AppCommands.AsGallery.Execute(null, window);
                await WaitFor(() => window.GalleryImage.IsVisible);
                window.GalleryImage.Focus();
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.IsKeyboardFocusWithin);
                Assert.True(window.DescriptionPanel.IsKeyboardFocusWithin);
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.GalleryImage.IsKeyboardFocusWithin);
                Assert.True(window.GalleryImage.IsKeyboardFocusWithin);

                // On a folder, the panel says it isn't described, and that's where focus goes.
                AppCommands.AsList.Execute(null, window);
                model.Navigate(model.Sections[2].Children[0].Id);
                model.SelectedRows = [model.Rows.Single(r => r.Name == "Trip")];
                window.FileList.Focus();
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.IsKeyboardFocusWithin);
                Assert.True(window.DescriptionPanel.NothingText.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task RedescribeWhileCorrecting_SavesTheCorrectionFirst()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                var row = model.Rows.Single(r => r.Name == "a.jpg");
                model.SelectedRows = [row];
                model.Descriptions!.Put(row.Item!, "accessible", "From Windows.");
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
                window.DescriptionPanel.DescriptionBox.Text = "My own words.";
                // Whatever happens next (asking to replace the correction, or saying describing isn't available here), it
                // never opens a real dialog in a test.
                var original = QuestionDialog.AskSafely;
                QuestionDialog.AskSafely = (_, _, _, _, _, _) => false;
                try { AppCommands.Redescribe.Execute(null, window); }
                finally { QuestionDialog.AskSafely = original; }
                Assert.Equal("My own words.", model.Descriptions.Get(row.Item!, "accessible"));
                Assert.True(model.Descriptions.IsEdited(row.Item!, "accessible"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public void ReplacingAnEdit_AsksWithRedescribeAndCancel_CancelTheSafeOne()
    {
        var original = QuestionDialog.AskSafely;
        string? asked = null;
        QuestionDialog.AskSafely = (_, title, question, details, yes, keep) => { asked = $"{title}|{question}|{details}|{yes}|{keep}"; return false; };
        try
        {
            Assert.False(MainWindow.AskToReplaceEdit(null!, "a.jpg"));
            Assert.Equal("Redescribe|Replace the description you edited?|a.jpg will be described again, and the new description replaces yours.|Redescribe|Cancel", asked);
        }
        finally
        {
            QuestionDialog.AskSafely = original;
        }
    }

    [WpfFact]
    public async Task SaveDescriptionsInPhotos_SaysItsPaused_InReadOnlyMode_UntilTurnedOff()
    {
        PhotoChanges.Allowed = false;
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.Settings.SaveDescriptionsInPhotos = true;
                window.SetReadOnly(true);
                Assert.Equal("Save Descriptions _in Photos (Paused in Read-Only Mode)", window.SaveInPhotosItem.Header);
                window.SetSaveInPhotos(false);
                Assert.Equal("Save Descriptions _in Photos", window.SaveInPhotosItem.Header);
            }
            finally
            {
                PhotoChanges.Allowed = true;
                window.Close();
            }
        }
    }

    private static async Task WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
    }
}
