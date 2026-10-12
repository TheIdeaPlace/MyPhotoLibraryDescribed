using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Read-only (#83): nothing touches a photo until the person turns it off and accepts the risk, and the title
/// bar says which it is. Also Ctrl+F9 and Ctrl+Shift+F9 for the status bar.</summary>
[Collection(SharedState.Name)]
public sealed class ReadOnlyTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];

    public ReadOnlyTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { Title = "Mine", Rating = 2 });
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        PhotoChanges.Allowed = true;
        _lib.Dispose();
    }

    private async Task<LibraryModel> StartAsync(Settings settings)
    {
        var model = new LibraryModel(settings, _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
        { Descriptions = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json")) };
        model.SelectionRequested += (ids, _) => model.SelectedRows = model.Rows.Where(r => ids.Contains(r.Id)).ToList();
        await model.StartAsync();
        await model.DetailFillTask;
        return model;
    }

    [Fact]
    public void ReadOnly_IsOnAtEveryLaunch_EvenAfterBeingTurnedOff()
    {
        Assert.True(new Settings().ReadOnly);
        var path = _lib.PathOf(@"data\old.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Settings from before read-only existed, even with Save Descriptions in Photos on.
        File.WriteAllText(path, """{ "SaveDescriptionsInPhotos": true }""");
        Assert.True(Settings.Load(path).ReadOnly);
        // Allowing changes lasts until the app quits: never saved, and a file saying otherwise is ignored.
        new Settings { ReadOnly = false }.Save(path);
        Assert.True(Settings.Load(path).ReadOnly);
        File.WriteAllText(path, """{ "ReadOnly": false }""");
        Assert.True(Settings.Load(path).ReadOnly);
    }

    [Fact]
    public async Task WhileReadOnly_TheWritersThemselvesRefuse_AndTheFileIsUntouched()
    {
        var path = _lib.PathOf("a.jpg");
        var before = File.ReadAllBytes(path);
        var item = new MediaItem(path, before.Length, File.GetCreationTime(path), File.GetLastWriteTime(path));
        PhotoChanges.Allowed = false;
        await Assert.ThrowsAsync<ReadOnlyException>(() => XmpFields.WriteAsync(path, "A dog.", "A dog.", ""));
        await Assert.ThrowsAsync<ReadOnlyException>(() => MetadataWriter.SetRatingAsync([item], 5));
        await Assert.ThrowsAsync<ReadOnlyException>(() => MetadataWriter.SetCaptionAsync([item], "A dog."));
        await Assert.ThrowsAsync<ReadOnlyException>(() => MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(item, "A dog.")]));
        await Assert.ThrowsAsync<ReadOnlyException>(() => XmpFields.WriteChangesAsync(path, new MetadataChanges { Stars = 3 }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [WpfFact]
    public async Task WhileReadOnly_RatingsFavoritesCaptionsAndSavingInPhotos_ChangeNothing_AndSayWhy()
    {
        var path = _lib.PathOf("a.jpg");
        var before = File.ReadAllBytes(path);
        PhotoChanges.Allowed = false;
        using var model = await StartAsync(new Settings { SaveDescriptionsInPhotos = true });
        Assert.True(model.IsReadOnly);
        var row = model.Rows.Single(r => r.Name == "a.jpg");
        model.SelectedRows = [row];
        await model.SetRatingAsync(5);
        await model.ToggleFavoriteAsync();
        await model.SaveCaptionAsync([row], "A dog.");
        await model.SaveCaptionsAsync([(row.Item!, "A dog.")], 0, 0);
        // A new description with Save Descriptions in Photos on: not written either.
        model.Descriptions!.Put(row.Item!, "accessible", "A described dog.");
        await model.CaptionsWritten;
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(PhotoChanges.ReadOnlyMessage, _announced);
    }

    [WpfFact]
    public async Task TurningReadOnlyOff_AsksEveryTime_AndTheTitleSaysWhichItIs()
    {
        PhotoChanges.Allowed = false;
        using var model = await StartAsync(new Settings());
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        var original = QuestionDialog.AskRisky;
        try
        {
            Assert.EndsWith("Photo Finder - Read-Only", window.Title);
            Assert.True(window.ReadOnlyItem.IsChecked);

            var asked = new List<string>();
            QuestionDialog.AskRisky = (_, _, question, _, yes, keep) => { asked.Add($"{question} [{yes}] [{keep}]"); return false; };
            window.SetReadOnly(false);
            Assert.Equal(["Let Photo Finder change your photos? [Allow Changes] [Keep Read-Only]"], asked);
            Assert.True(model.IsReadOnly);
            Assert.False(PhotoChanges.Allowed);
            Assert.True(window.ReadOnlyItem.IsChecked);

            QuestionDialog.AskRisky = (_, _, _, _, _, _) => true;
            window.SetReadOnly(false);
            Assert.False(model.IsReadOnly);
            Assert.True(PhotoChanges.Allowed);
            Assert.EndsWith("Photo Finder - Editing", window.Title);
            // Not remembered: the next launch is read-only again.
            Assert.True(Settings.Load(_lib.PathOf(@"data\Settings.json")).ReadOnly);
            Assert.Contains("Editing. Photo Finder can change your photos", _announced);

            // Going back to read-only doesn't ask.
            QuestionDialog.AskRisky = (_, _, _, _, _, _) => throw new InvalidOperationException("Asked to turn read-only on");
            window.SetReadOnly(true);
            Assert.True(model.IsReadOnly);
            Assert.False(PhotoChanges.Allowed);
            Assert.EndsWith("Photo Finder - Read-Only", window.Title);

            // And off again asks again.
            asked.Clear();
            QuestionDialog.AskRisky = (_, _, question, _, _, _) => { asked.Add(question); return false; };
            window.SetReadOnly(false);
            Assert.Single(asked);
        }
        finally
        {
            QuestionDialog.AskRisky = original;
            window.Close();
        }
    }

    [WpfFact]
    public async Task WhileReadOnly_CommandsThatChangePhotosSayWhy_WithoutAsking()
    {
        PhotoChanges.Allowed = false;
        using var model = await StartAsync(new Settings());
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        var original = QuestionDialog.Ask;
        try
        {
            QuestionDialog.Ask = (_, _, _, _, _) => throw new InvalidOperationException("Asked while read-only");
            var row = model.Rows.Single(r => r.Name == "a.jpg");
            model.Descriptions!.Put(row.Item!, "accessible", "A dog.");
            await window.UseDescriptionAsCaptionAsync([row]);
            window.SetSaveInPhotos(true);
            Assert.False(model.Settings.SaveDescriptionsInPhotos);
            Assert.False(window.SaveInPhotosItem.IsChecked);
            Assert.Equal(2, _announced.Count(a => a == PhotoChanges.ReadOnlyMessage));
        }
        finally
        {
            QuestionDialog.Ask = original;
            window.Close();
        }
    }

    [WpfFact]
    public void TheRiskQuestion_HasKeepReadOnlyAsTheDefault()
    {
        var dialog = new QuestionDialog("Allow Changes to Photos", "Let it change your photos?", "Risky.", "Allow Changes", "Keep Read-Only");
        Assert.True(dialog.CancelButton.IsDefault);
        Assert.False(dialog.YesButton.IsDefault);
        Assert.Equal("Keep Read-Only", dialog.CancelButton.Content);
        Assert.Equal("Let it change your photos? Risky.", System.Windows.Automation.AutomationProperties.GetHelpText(dialog.CancelButton));
        // An ordinary question keeps yes as the default.
        var plain = new QuestionDialog("Save", "Save?", "Details.", "Save in Photos");
        Assert.True(plain.YesButton.IsDefault);
    }

    [WpfFact]
    public async Task SaveInPhotos_ReadsAsPausedWhileReadOnly_AndComesBackWhenChangesAreAllowed()
    {
        PhotoChanges.Allowed = false;
        using var model = await StartAsync(new Settings { SaveDescriptionsInPhotos = true });
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        var original = QuestionDialog.AskRisky;
        try
        {
            Assert.False(window.SaveInPhotosItem.IsChecked);
            Assert.Equal("Paused while read-only", System.Windows.Automation.AutomationProperties.GetHelpText(window.SaveInPhotosItem));
            QuestionDialog.AskRisky = (_, _, _, _, _, _) => true;
            window.SetReadOnly(false);
            Assert.True(window.SaveInPhotosItem.IsChecked);
            Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(window.SaveInPhotosItem));
            Assert.True(model.Settings.SaveDescriptionsInPhotos);
        }
        finally
        {
            QuestionDialog.AskRisky = original;
            window.Close();
        }
    }

    [WpfFact]
    public async Task CtrlF9ReadsTheStatusBar_AndCtrlShiftF9GoesToIt()
    {
        using var model = await StartAsync(new Settings());
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.Contains(AppCommands.ReadStatusBar.InputGestures.OfType<KeyGesture>(), g => g.Key == Key.F9 && g.Modifiers == ModifierKeys.Control);
            Assert.Contains(AppCommands.GoToStatusBar.InputGestures.OfType<KeyGesture>(),
                g => g.Key == Key.F9 && g.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            _announced.Clear();
            window.ReadStatusBar();
            Assert.Equal([window.StatusBarText.Text], _announced);
            Assert.False(window.StatusBarText.IsKeyboardFocused);

            window.FocusStatusBar();
            Assert.True(window.StatusBarText.IsKeyboardFocused);
        }
        finally
        {
            window.Close();
        }
    }
}
