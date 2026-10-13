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
    public void TheDefault_IsPositionOnly()
    {
        Assert.Equal([GalleryLabels.Position], GalleryLabels.DefaultOrder);
        Assert.Equal("12 of 400", GalleryLabels.Text(Row("IMG_1.jpg"), 12, 400, GalleryLabels.DefaultOrder));
        Assert.Equal(["position"], GalleryLabels.Clean(null));
        Assert.Equal(["position"], GalleryLabels.Clean(["nonsense"]));
        Assert.Equal(["name", "caption"], GalleryLabels.Clean(["name", "caption", "name", "description"]));
    }

    [Fact]
    public void ChosenItems_AreReadInOrder_WithTheListsPrefixes_SkippingEmptyOnes()
    {
        var row = Row("IMG_1.jpg", caption: "At the beach");
        Assert.Equal("3 of 10, IMG_1.jpg, Caption: At the beach", GalleryLabels.Text(row, 3, 10, ["position", "name", "caption", "keywords"]));
        Assert.Equal("Caption: At the beach, 3 of 10", GalleryLabels.Text(row, 3, 10, ["caption", "position"]));
        // Nothing to say: the name, so moving never says nothing.
        Assert.Equal("IMG_2.jpg", GalleryLabels.Text(Row("IMG_2.jpg"), 1, 1, ["caption", "keywords"]));
        Assert.Equal(["Position", "Name", "Kind", "Date Taken", "Dimensions", "Caption", "Keywords", "Rating", "Place Taken"],
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
            gallery.ShownList.SelectedIndex = 0;
            gallery.Remove();
            Assert.Equal(["position"], gallery.Shown);
            Assert.Equal("Position is the only label shown. Add another before removing it.", _announced[^1]);

            gallery.AvailableList.SelectedIndex = gallery.AvailableList.Items.Cast<string>().ToList().IndexOf("Name");
            gallery.Add();
            Assert.Equal(["position", "name"], gallery.Shown);
            Assert.Equal("2 of 5, IMG_1.jpg", gallery.PreviewBox.Text);
            gallery.Reset();
            Assert.Equal(["position"], dialog.ShownGalleryLabels);
            Assert.Equal("Labels reset to the default: Position.", _announced[^1]);
            Assert.Equal("Available labels", System.Windows.Automation.AutomationProperties.GetName(gallery.AvailableList));
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
}
