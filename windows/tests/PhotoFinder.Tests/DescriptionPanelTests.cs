using System.Windows;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>The Description panel and column (#50), as on the Mac: corrections are saved, marked "Edited by you" and
/// never replaced without asking.</summary>
[Collection(SharedState.Name)]
public sealed class DescriptionPanelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];

    public DescriptionPanelTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 2) });
        _lib.AddJpeg("b.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 1) });
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private static MediaItem ItemFor(string path)
    {
        var info = new FileInfo(path);
        return new MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime);
    }

    [Fact]
    public void Corrections_AreMarked_Kept_AndNotReplacedByWindows()
    {
        var file = _lib.PathOf(@"data\Descriptions.json");
        var store = new DescriptionStore(file);
        var a = ItemFor(_lib.PathOf("a.jpg"));
        store.Put(a, "accessible", "Made by Windows.");
        Assert.False(store.IsEdited(a, "accessible"));
        store.Put(a, "accessible", "My correction.", edited: true);
        Assert.True(store.IsEdited(a, "accessible"));
        Assert.False(store.PutUnlessEdited(a, "accessible", "Windows again."));
        Assert.Equal("My correction.", store.Get(a, "accessible"));
        store.Save();

        // After a restart, from the separate file beside Descriptions.json.
        Assert.True(File.Exists(_lib.PathOf(@"data\EditedDescriptions.json")));
        var again = new DescriptionStore(file);
        Assert.True(again.IsEdited(a, "accessible"));
        // Describe Again, agreed to: Windows' description replaces it and the mark goes.
        again.Put(a, "accessible", "Windows, asked again.");
        Assert.False(again.IsEdited(a, "accessible"));
    }

    private async Task<(LibraryModel Model, MainWindow Window)> StartAsync()
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var model = new LibraryModel(new Settings { AutoDescribe = false }, _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\m.json")), () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
        { Descriptions = store };
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((model.Rows.Count < 2 || model.DetailProgress is not null) && DateTime.UtcNow < until)
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        return (model, window);
    }

    [WpfFact]
    public async Task ThePanel_FollowsThePhoto_SaysWhereItsDescriptionStands_AndSavesCorrections()
    {
        var (model, window) = await StartAsync();
        try
        {
            var panel = window.DescriptionPanel;
            Assert.True(panel.IsVisible);
            Assert.Equal("Select a photo to see its description.", panel.NothingText.Text);

            model.SelectedRows = [model.Rows.Single(r => r.Name == "a.jpg")];
            var a = model.FocusedRow!;
            Assert.Same(a, panel.Row);
            Assert.Equal("a.jpg", panel.NameText.Text);
            Assert.Equal("Not described yet", panel.StatusText.Text);

            // A description arriving shows at once.
            model.Descriptions!.Put(a.Item!, "accessible", "Made by Windows.");
            Assert.Equal("Made by Windows.", panel.DescriptionBox.Text);
            Assert.Equal("Saved description", panel.StatusText.Text);

            // A correction is saved and marked.
            panel.DescriptionBox.Text = "My correction.";
            panel.SaveIfChanged();
            Assert.Equal("Edited by you", panel.StatusText.Text);
            Assert.True(model.Descriptions.IsEdited(a.Item!, "accessible"));
            Assert.Contains("Description saved", _announced);
            Assert.Equal("My correction.", a.Description);

            // Emptying the box keeps the saved one.
            panel.DescriptionBox.Text = "  ";
            panel.SaveIfChanged();
            Assert.Contains("A description can't be empty. The saved one is kept.", _announced);
            Assert.Equal("My correction.", panel.DescriptionBox.Text);

            // Typing, then a description arriving: what's typed stays, and is saved on moving on.
            panel.DescriptionBox.Text = "Still typing";
            model.Descriptions.Put(a.Item!, "brief", "Another type.");
            Assert.Equal("Still typing", panel.DescriptionBox.Text);
            model.SelectedRows = [model.Rows.Single(r => r.Name == "b.jpg")];
            Assert.Equal("Still typing", model.Descriptions.Get(a.Item!, "accessible"));

            // Hidden: the gallery shows the description under the photo instead.
            window.ShowDescriptionPanel(false, announce: true);
            Assert.False(panel.IsVisible);
            Assert.Contains("Description panel hidden", _announced);
            Assert.False(Settings.Load(_lib.PathOf(@"data\Settings.json")).ShowDescriptionPanel);
            window.ShowDescriptionPanel(true, announce: false);
            Assert.True(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task TheDescriptionColumn_ShowsSavedDescriptions_AndIsReadWithTheRow()
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        store.Put(ItemFor(_lib.PathOf("a.jpg")), "accessible", "A test picture.");
        store.Save();
        var (model, window) = await StartAsync();
        try
        {
            Assert.NotNull(Columns.Find("description"));
            Assert.False(Columns.Find("description")!.VisibleByDefault);
            var a = model.Rows.Single(r => r.Name == "a.jpg");
            Assert.Equal("A test picture.", a.Description);
            Assert.Equal("A test picture.", a.ColumnText("description"));
            Assert.Contains("Description: A test picture.", a.SpokenWith(["name", "description"]));
        }
        finally
        {
            window.Close();
        }
    }
}
