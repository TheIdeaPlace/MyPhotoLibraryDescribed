using System.Windows.Automation.Peers;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Next and previous photo from anywhere, the Description panel's preview in list view, and how much of the
/// library is described (#119), as on the Mac.</summary>
[Collection(SharedState.Name)]
public sealed class NextPhotoAndPreviewTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];
    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public NextPhotoAndPreviewTests()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Announcer.Observer = _announced.Add;
        // In the library folder's view, sorted by name: a.jpg, b.mp4, c.jpg, then the folder Trip.
        _lib.AddJpeg("a.jpg");
        _lib.AddFile("b.mp4", "video");
        // A different size from a.jpg, so the preview shows which one it loaded.
        _lib.AddJpeg("c.jpg", new TestLibrary.JpegOptions { Width = 96, Height = 72 });
        _lib.AddJpeg(@"Trip\d.jpg");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        System.Globalization.CultureInfo.CurrentCulture = _culture;
        DescriptionPanel.PreviewDelay = TimeSpan.FromMilliseconds(150);
        _lib.Dispose();
    }

    private async Task<(LibraryModel Model, MainWindow Window)> OpenAsync()
    {
        var model = new LibraryModel(new Settings { AutoDescribe = false }, _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
            { Descriptions = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json")) };
        await model.StartAsync();
        var window = new MainWindow(model) { ShowActivated = false, Height = 900 };
        window.Show();
        return (model, window);
    }

    private static async Task WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
    }

    [Fact]
    public void TheKeys_AreAltRightAndAltLeft_WhichWorkInATextBox()
    {
        Assert.Contains(AppCommands.NextPhoto.InputGestures.OfType<KeyGesture>(), g => g.Key == Key.Right && g.Modifiers == ModifierKeys.Alt);
        Assert.Contains(AppCommands.PreviousPhoto.InputGestures.OfType<KeyGesture>(), g => g.Key == Key.Left && g.Modifiers == ModifierKeys.Alt);
    }

    [WpfFact]
    public async Task NextAndPrevious_SkipVideosAndFolders_LeaveFocusInTheDescriptionBox_AndSayWhereTheyAre()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.Navigate(model.Sections[2].Children[0].Id);
                var names = model.Rows.Select(r => r.Name).ToList();
                Assert.Equal(["a.jpg", "b.mp4", "c.jpg", "Trip"], names.Order());
                // The photos in the order shown, which skips the folder and the video.
                var photos = names.Where(n => n.EndsWith(".jpg")).ToList();
                model.SelectedRows = [model.Rows.Single(r => r.Name == photos[0])];
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);

                _announced.Clear();
                AppCommands.NextPhoto.Execute(null, window);
                Assert.Equal(photos[1], model.FocusedRow!.Name);
                Assert.Equal($"{photos[1]}, {names.IndexOf(photos[1]) + 1} of 4", _announced.Single());
                // Focus stayed in the box, which now shows the new photo.
                Assert.True(window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
                Assert.Equal(photos[1], window.DescriptionPanel.Row!.Name);

                AppCommands.NextPhoto.Execute(null, window);
                Assert.Equal("No next photo", _announced[^1]);
                Assert.Equal(photos[1], model.FocusedRow!.Name);

                AppCommands.PreviousPhoto.Execute(null, window);
                Assert.Equal(photos[0], model.FocusedRow!.Name);
                AppCommands.PreviousPhoto.Execute(null, window);
                Assert.Equal("No previous photo", _announced[^1]);
                Assert.True(window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task FromTheList_TheRowSpeaksForItself_SoNothingElseIsSaid()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.Navigate(model.Sections[2].Children[0].Id);
                var photos = model.Rows.Where(r => r.Name.EndsWith(".jpg")).ToList();
                model.SelectedRows = [photos[0]];
                window.FileList.Focus();
                await WaitFor(() => window.FileList.IsKeyboardFocusWithin);
                _announced.Clear();
                AppCommands.NextPhoto.Execute(null, window);
                Assert.Equal(photos[1].Name, model.FocusedRow!.Name);
                Assert.Empty(_announced);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task ThePreview_ShowsInListView_NotInTheGallery_AndScreenReadersSkipIt()
    {
        DescriptionPanel.PreviewDelay = TimeSpan.Zero;
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.SelectedRows = [model.Rows.First(r => r.Name == "a.jpg")];
                await WaitFor(() => window.DescriptionPanel.Preview is not null);
                Assert.NotNull(window.DescriptionPanel.Preview);
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(window.DescriptionPanel.PreviewImage));

                AppCommands.AsGallery.Execute(null, window);
                Assert.Null(window.DescriptionPanel.Preview);
                AppCommands.AsList.Execute(null, window);
                await WaitFor(() => window.DescriptionPanel.Preview is not null);
                Assert.NotNull(window.DescriptionPanel.Preview);

                // A short window gives the description the room.
                window.Height = 300;
                window.UpdateLayout();
                await WaitFor(() => window.DescriptionPanel.Preview is null);
                Assert.Null(window.DescriptionPanel.Preview);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task FromTheNoteOnAVideo_NextPhotoGoesToTheDescriptionBox_RatherThanLosingFocus()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.Navigate(model.Sections[2].Children[0].Id);
                var names = model.Rows.Select(r => r.Name).ToList();
                model.SelectedRows = [model.Rows.Single(r => r.Name == "b.mp4")];
                window.GoToDescriptionOrBack();
                await WaitFor(() => window.DescriptionPanel.NothingText.IsKeyboardFocused);
                // Whichever way the photos lie from the video.
                var command = names.Skip(names.IndexOf("b.mp4") + 1).Any(n => n.EndsWith(".jpg")) ? AppCommands.NextPhoto : AppCommands.PreviousPhoto;
                command.Execute(null, window);
                Assert.EndsWith(".jpg", model.FocusedRow!.Name);
                await WaitFor(() => window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
                Assert.True(window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task ThePreview_HoldsItsPlace_KeepsTheLastPictureUntilTheNext_AndWaitsWhileHidden()
    {
        DescriptionPanel.PreviewDelay = TimeSpan.FromMilliseconds(100);
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                var panel = window.DescriptionPanel;
                var a = model.Rows.First(r => r.Name == "a.jpg");
                var c = model.Rows.First(r => r.Name == "c.jpg");
                model.SelectedRows = [a];
                // The place is held from the start, so nothing below it moves when the picture arrives.
                Assert.True(panel.ShowsPlaceholder);
                await WaitFor(() => panel.Preview is not null);
                var first = panel.Preview;

                // Two quick steps: a's picture stays meanwhile, and only the last photo's loads.
                model.SelectedRows = [c];
                Assert.Same(first, panel.Preview);
                model.SelectedRows = [a];
                // Long enough for c's load to have finished, had it not been dropped.
                await Task.Delay(400);
                Assert.Equal("a.jpg", panel.Row!.Name);
                Assert.Equal(64, ((System.Windows.Media.Imaging.BitmapSource)panel.Preview!).PixelWidth);

                // Hidden, nothing loads; shown again, it does.
                window.ShowDescriptionPanel(false, announce: false);
                model.SelectedRows = [c];
                await Task.Delay(300);
                Assert.Null(panel.PreviewImage.Source);
                window.ShowDescriptionPanel(true, announce: false);
                await WaitFor(() => panel.Preview is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 96 });
                Assert.Equal(96, ((System.Windows.Media.Imaging.BitmapSource)panel.Preview!).PixelWidth);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [Theory]
    [InlineData(159, 400, "39% described, 159 of 400 photos.")]
    [InlineData(399, 400, "99% described, 399 of 400 photos.")]
    [InlineData(400, 400, "100% described, 400 of 400 photos.")]
    [InlineData(0, 1, "0% described, 0 of 1 photo.")]
    [InlineData(1234, 7465, "16% described, 1,234 of 7,465 photos.")]
    [InlineData(0, 0, "No photos in the library yet.")]
    public void CheckSystem_SaysHowMuchOfTheLibraryIsDescribed_RoundedDown(int described, int total, string expected) =>
        Assert.Equal(expected, SystemCheck.CoverageText(described, total));

    [WpfFact]
    public async Task CheckSystem_CountsTheLibrarysPhotos_NotVideos()
    {
        var (model, window) = await OpenAsync();
        using (model)
        {
            try
            {
                model.Descriptions!.Put(model.AllItems.Single(i => i.Name == "a.jpg"), "accessible", "Described.");
                var check = await window.RunSystemCheckAsync();
                Assert.Contains("Saved descriptions: 33% described, 1 of 3 photos.", check.Items.Select(i => i.Line));
            }
            finally
            {
                window.Close();
            }
        }
    }
}
