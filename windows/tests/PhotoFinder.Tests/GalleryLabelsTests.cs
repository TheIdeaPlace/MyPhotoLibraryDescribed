using System.Windows.Automation.Peers;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>View > Labels > Gallery (#38), as on the Mac (#140): what the gallery says when you move to a photo.</summary>
[Collection(SharedState.Name)]
public sealed class GalleryLabelsTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];

    public GalleryLabelsTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("a.jpg");
        _lib.AddJpeg("b.jpg");
        _lib.AddJpeg("c.jpg");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private static FileRow Row(string name, string caption = "")
    {
        var item = new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now)
            { Metadata = MediaMetadata.Empty with { Caption = caption, Width = 4000, Height = 3000 } };
        return FileRow.ForItem(item);
    }

    [Fact]
    public void TheDefault_IsPositionThenDescription()
    {
        Assert.Equal([GalleryLabels.Position, GalleryLabels.Description], GalleryLabels.DefaultOrder);
        Assert.Equal("12 of 400", GalleryLabels.Text(Row("IMG_1.jpg"), 12, 400, GalleryLabels.DefaultOrder));
        Assert.Equal("12 of 400, A dog on a beach.",
            GalleryLabels.Text(Row("IMG_1.jpg"), 12, 400, GalleryLabels.DefaultOrder, "A dog on a beach."));
        Assert.Equal(["position", "description"], GalleryLabels.Clean(null));
        Assert.Equal(["position", "description"], GalleryLabels.Clean(["nonsense"]));
        Assert.Equal(["name", "caption", "description"], GalleryLabels.Clean(["name", "caption", "name", "description"]));
    }

    [Fact]
    public void TheDescription_IsReadInItsPlace_WithoutAFullStopAndComma()
    {
        // #247, as on the Mac.
        var row = Row("IMG_1.jpg");
        Assert.Equal("A dog on a beach at sunset. 12 of 400",
            GalleryLabels.Text(row, 12, 400, ["description", "position"], "A dog on a beach at sunset."));
        Assert.Equal("12 of 400, A dog on a beach, IMG_1.jpg",
            GalleryLabels.Text(row, 12, 400, ["position", "description", "name"], "  A dog on a beach \n"));
        // Not described yet: left out, and the name when that leaves nothing.
        Assert.Equal("12 of 400", GalleryLabels.Text(row, 12, 400, ["description", "position"]));
        Assert.Equal("IMG_1.jpg", GalleryLabels.Text(row, 12, 400, ["description"]));
    }

    [Fact]
    public void LabelsSavedBeforeDescriptionWasALabel_GetItAtTheEnd_Once()
    {
        Assert.Equal(["name", "position", "description"], GalleryLabels.Migrate(["name", "position"], 0));
        Assert.Equal(["description", "position"], GalleryLabels.Migrate(["description", "position"], 0));
        Assert.Equal(["position"], GalleryLabels.Migrate(["position"], GalleryLabels.Version));
        Assert.Null(GalleryLabels.Migrate(null, 0));

        var path = _lib.PathOf(@"data\Old.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "GalleryLabels": ["position"] }""");
        var old = Settings.Load(path);
        Assert.Equal(["position", "description"], old.GalleryLabels);
        // Removed since: stays removed.
        old.GalleryLabels = ["position"];
        old.Save(path);
        Assert.Equal(["position"], Settings.Load(path).GalleryLabels);
    }

    [Fact]
    public void ChosenItems_AreReadInOrder_WithTheListsPrefixes_SkippingEmptyOnes()
    {
        var row = Row("IMG_1.jpg", caption: "At the beach");
        Assert.Equal("3 of 10, IMG_1.jpg, Caption: At the beach", GalleryLabels.Text(row, 3, 10, ["position", "name", "caption", "keywords"]));
        Assert.Equal("Caption: At the beach, 3 of 10", GalleryLabels.Text(row, 3, 10, ["caption", "position"]));
        // Nothing to say: the name, so moving never says nothing.
        Assert.Equal("IMG_2.jpg", GalleryLabels.Text(Row("IMG_2.jpg"), 1, 1, ["caption", "keywords"]));
        Assert.Equal(["Position", "Description", "Name", "Kind", "Date Taken", "Dimensions", "Caption", "Keywords", "Rating", "Place Taken"],
            GalleryLabels.All.Select(a => a.Title));
    }

    [WpfFact]
    public void TheGalleryTab_KeepsOneLabel_AndResetsToPosition()
    {
        var dialog = new LabelsDialog(Columns.DefaultOrder, GalleryLabels.DefaultOrder, Row("IMG_1.jpg"), 2, 5, galleryFirst: true) { ShowActivated = false };
        dialog.Show();
        try
        {
            Assert.Same(dialog.GalleryTab, dialog.Tabs.SelectedItem);
            var gallery = dialog.GalleryEditor;
            Assert.Equal("2 of 5", gallery.PreviewBox.Text);
            gallery.ShownList.SelectedIndex = 1;
            gallery.Remove();
            Assert.Equal(["position"], gallery.Shown);
            gallery.ShownList.SelectedIndex = 0;
            gallery.Remove();
            Assert.Equal(["position"], gallery.Shown);
            Assert.Equal("Position is the only label shown. Add another before removing it.", _announced[^1]);

            gallery.AvailableList.SelectedIndex = gallery.AvailableList.Items.Cast<string>().ToList().IndexOf("Name");
            gallery.Add();
            Assert.Equal(["position", "name"], gallery.Shown);
            Assert.Equal("2 of 5, IMG_1.jpg", gallery.PreviewBox.Text);
            gallery.Reset();
            Assert.Equal(["position", "description"], dialog.ShownGalleryLabels);
            Assert.Equal("Labels reset to the default: Position, Description.", _announced[^1]);
            Assert.Equal("Available labels", System.Windows.Automation.AutomationProperties.GetName(gallery.AvailableList));

        }
        finally
        {
            dialog.Close();
        }
    }

    [WpfFact]
    public void TheExplanation_IsSaidOnceOnOpening_AndOnChangingTab_NeverWithTheListsItems()
    {
        var dialog = new LabelsDialog(Columns.DefaultOrder, GalleryLabels.DefaultOrder, Row("IMG_1.jpg"), 2, 5, galleryFirst: false) { ShowActivated = false };
        _announced.Clear();
        dialog.Show();
        try
        {
            Assert.Single(_announced, a => a.StartsWith("Choose which columns", StringComparison.Ordinal));
            foreach (var editor in new[] { dialog.ListEditor, dialog.GalleryEditor })
                foreach (var list in new System.Windows.Controls.ListBox[] { editor.ShownList, editor.AvailableList })
                    Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(list));
            Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(dialog.ListTab));
            _announced.Clear();
            dialog.Tabs.SelectedItem = dialog.GalleryTab;
            Assert.Equal(["Choose what's read, and in what order, when you move to a photo in the gallery. A photo not described yet is read without its description, which follows once it's ready."], _announced);
            // Moving in a list says nothing more.
            _announced.Clear();
            dialog.GalleryEditor.ShownList.SelectedIndex = 0;
            Assert.Empty(_announced);
        }
        finally
        {
            dialog.Close();
        }
    }

    [WpfFact]
    public async Task InTheGallery_MovingSaysTheLabels_AndThePictureIsNamedTheSame()
    {
        var model = new LibraryModel(new Settings { AutoDescribe = false, ViewMode = ViewMode.Gallery }, _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\m.json")), () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        using (model)
        {
            await model.StartAsync();
            var window = new MainWindow(model) { ShowActivated = false };
            window.Show();
            try
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (model.FocusedRow is null && DateTime.UtcNow < until) await Task.Delay(20);
                _announced.Clear();
                model.MoveSelection(1);
                Assert.Equal($"2 of {model.Rows.Count}", _announced.Single());
                await Task.Delay(100);
                var peer = UIElementAutomationPeer.CreatePeerForElement(window.GalleryImage)!;
                Assert.Equal($"2 of {model.Rows.Count}", peer.GetName());

                model.SetGalleryLabels(["name", "position"]);
                _announced.Clear();
                model.MoveSelection(-1);
                Assert.Equal($"{model.Rows[0].Name}, 1 of {model.Rows.Count}", _announced.Single());

                // The list still says the name and position.
                model.ViewMode = ViewMode.List;
                Assert.Equal($"{model.Rows[1].Name}, 2 of {model.Rows.Count}", model.MoveText(1));
                Assert.Equal(["name", "position"], Settings.Load(_lib.PathOf(@"data\Settings.json")).GalleryLabels);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [WpfFact]
    public async Task InTheGallery_ASavedDescriptionIsReadWithTheLabels_AndNotSpokenAgain()
    {
        // #247: moving reads "12 of 400, A cat on a step." at once; automatic describing then says nothing more. Without
        // Description, the gallery doesn't read descriptions; the list is as it was.
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var model = new LibraryModel(new Settings { AutoDescribe = false, ViewMode = ViewMode.Gallery }, _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\m.json")), () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
            { Descriptions = store };
        using (model)
        {
            await model.StartAsync();
            var window = new MainWindow(model) { ShowActivated = false };
            window.Show();
            try
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (model.FocusedRow is null && DateTime.UtcNow < until) await Task.Delay(20);
                var second = model.Rows[1].Item!;
                store.Put(second, "accessible", "A cat on a step.");
                Assert.True(model.ReadsDescriptionAloud(second.Path), "not read on moving yet");

                _announced.Clear();
                model.MoveSelection(1);
                Assert.Equal($"2 of {model.Rows.Count}, A cat on a step.", _announced.Single());
                Assert.False(model.ReadsDescriptionAloud(second.Path), "read already");

                model.SetGalleryLabels(["description", "position"]);
                Assert.Equal($"A cat on a step. 2 of {model.Rows.Count}", model.MoveText(1));

                model.SetGalleryLabels(["position"]);
                Assert.Equal($"2 of {model.Rows.Count}", model.MoveText(1));
                Assert.False(model.ReadsDescriptionAloud(model.Rows[0].Item!.Path), "the gallery isn't reading descriptions");

                model.ViewMode = ViewMode.List;
                Assert.True(model.ReadsDescriptionAloud(second.Path), "the list speaks descriptions as before");
            }
            finally
            {
                window.Close();
            }
        }
    }
}
