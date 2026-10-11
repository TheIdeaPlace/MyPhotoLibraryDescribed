using System.Windows.Controls;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

public class ColumnOrderTests
{
    [Fact]
    public void Clean_DropsUnknownAndRepeated_AndAlwaysKeepsName()
    {
        Assert.Equal(["caption", "name", "date"], Columns.Clean(["caption", "nonsense", "name", "date", "caption"]));
        Assert.Equal(["name", "caption"], Columns.Clean(["caption"]));
        Assert.Equal(Columns.DefaultOrder, Columns.Clean([]));
        Assert.Equal(Columns.DefaultOrder, Columns.Clean(["nonsense"]));
        Assert.Equal(Columns.DefaultOrder, Columns.Clean(null));
    }

    [Fact]
    public void DefaultOrder_IsNameThenTheDefaultColumns()
    {
        Assert.Equal(["name", "date", "kind", "caption", "dimensions", "duration", "favorite"], Columns.DefaultOrder);
    }

    [Fact]
    public void SettingsFromBeforeReordering_KeepTheirOnOffChoices()
    {
        var old = new Settings();
        old.Columns["caption"] = false;
        old.Columns["keywords"] = true;
        Assert.Equal(["name", "date", "kind", "dimensions", "duration", "favorite", "keywords"], old.ShownColumnIds());
    }

    [Fact]
    public void ColumnOrder_RoundTripsThroughSaving()
    {
        using var lib = new TestLibrary();
        var path = lib.PathOf("Settings.json");
        new Settings { ColumnOrder = ["caption", "name", "rating"] }.Save(path);
        Assert.Equal(["caption", "name", "rating"], Settings.Load(path).ShownColumnIds());
    }

    [Fact]
    public void ARow_IsReadInTheChosenOrder()
    {
        var row = FileRow.ForItem(new MediaItem(@"C:\p\IMG_1.jpg", 1, new DateTime(2024, 3, 3), new DateTime(2024, 3, 3))
        {
            Metadata = new MediaMetadata { Caption = "Sunset", Rating = 5 },
        });
        Assert.Equal("Caption: Sunset, IMG_1.jpg, Favorite", row.SpokenWith(["caption", "name", "favorite"]));
        // Name is read first when it isn't listed.
        Assert.Equal("IMG_1.jpg, Caption: Sunset", row.SpokenWith(["caption"]));
    }
}

[Collection(SharedState.Name)]
public sealed class ColumnChoosingTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];

    public ColumnChoosingTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("IMG_1.jpg", new TestLibrary.JpegOptions { Title = "Sunset", DateTaken = new DateTime(2024, 3, 3) });
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private async Task<LibraryModel> StartAsync()
    {
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        await model.StartAsync();
        await model.DetailFillTask;
        return model;
    }

    [WpfFact]
    public async Task SetColumnOrder_ChangesWhatEachRowSays_AndIsSaved()
    {
        using var model = await StartAsync();
        var row = model.Rows.Single();
        model.SetColumnOrder(["caption", "name", "kind"]);
        Assert.Equal("Caption: Sunset, IMG_1.jpg, Photo", row.AccessibleName);
        Assert.Equal(["caption", "name", "kind"], model.ShownColumns.Select(c => c.Id));
        Assert.Equal(["caption", "name", "kind"], Settings.Load(_lib.PathOf(@"data\Settings.json")).ShownColumnIds());
    }

    [WpfFact]
    public async Task TurningAColumnOn_AddsItAtTheEnd_AndOffTakesItOut()
    {
        using var model = await StartAsync();
        model.SetColumnOrder(["name", "caption"]);
        model.SetColumnVisible(Columns.Find("rating")!, true);
        Assert.Equal(["name", "caption", "rating"], model.ShownColumns.Select(c => c.Id));
        model.SetColumnVisible(Columns.Find("caption")!, false);
        Assert.Equal(["name", "rating"], model.ShownColumns.Select(c => c.Id));
        model.SetColumnVisible(Columns.NameColumn, false);
        Assert.Contains(Columns.NameColumn, model.ShownColumns);
    }

    private ColumnsDialog Dialog(IEnumerable<string> shown, FileRow? sample = null)
    {
        var dialog = new ColumnsDialog(shown, sample) { ShowActivated = false };
        dialog.Show();
        return dialog;
    }

    private static List<string> ShownTitles(ColumnsDialog d) => d.ShownList.Items.Cast<string>().ToList();

    [WpfFact]
    public void Moving_SaysWhereTheColumnWent_AndKeepsItSelected()
    {
        var d = Dialog(["name", "date", "caption"]);
        try
        {
            d.ShownList.SelectedIndex = 2;
            d.Move(-1);
            Assert.Equal(["name", "caption", "date"], d.ShownColumns);
            Assert.Equal(1, d.ShownList.SelectedIndex);
            Assert.Contains("Caption moved up, 2 of 3.", _announced);

            d.MoveToEnd(top: true);
            Assert.Equal(["caption", "name", "date"], d.ShownColumns);
            Assert.Contains("Caption moved to top, 1 of 3.", _announced);

            d.Move(-1);
            Assert.Contains("Caption is already at the top.", _announced);

            d.MoveToEnd(top: false);
            Assert.Equal(["name", "date", "caption"], d.ShownColumns);
            Assert.Equal(["Name", "Date Taken", "Caption"], ShownTitles(d));
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public void AddingAndRemoving_UpdateBothLists_AndNameStays()
    {
        var d = Dialog(["name", "date"]);
        try
        {
            var available = d.AvailableList.Items.Cast<string>().ToList();
            Assert.DoesNotContain("Name", available);
            d.AvailableList.SelectedIndex = available.IndexOf("Rating");
            d.AddColumn();
            Assert.Equal(["name", "date", "rating"], d.ShownColumns);
            Assert.Contains("Rating added, 3 of 3.", _announced);
            Assert.DoesNotContain("Rating", d.AvailableList.Items.Cast<string>());

            d.ShownList.SelectedIndex = 1;
            d.RemoveColumn();
            Assert.Equal(["name", "rating"], d.ShownColumns);
            Assert.Contains("Date Taken removed. 2 columns shown.", _announced);

            d.ShownList.SelectedIndex = 0;
            d.RemoveColumn();
            Assert.Equal(["name", "rating"], d.ShownColumns);
            Assert.Contains("Name is always shown. It can be moved.", _announced);
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public void WithEveryColumnShown_AvailableSaysSo()
    {
        var d = Dialog(Columns.All.Select(c => c.Id));
        try
        {
            Assert.Equal(["Every column is shown."], d.AvailableList.Items.Cast<string>());
            d.AddColumn();
            Assert.Contains("Every column is shown already.", _announced);
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public void Reset_GoesBackToTheDefaults()
    {
        var d = Dialog(["rating", "name"]);
        try
        {
            d.ResetColumns();
            Assert.Equal(Columns.DefaultOrder, d.ShownColumns);
            Assert.Contains($"Columns reset to the default: {Columns.DefaultOrder.Count} shown, Name first.", _announced);
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public void ThePreview_ReadsTheSelectedPhotoInTheNewOrder()
    {
        var row = FileRow.ForItem(new MediaItem(@"C:\p\IMG_1.jpg", 1, DateTime.Now, DateTime.Now) { Metadata = new MediaMetadata { Caption = "Sunset" } });
        var d = Dialog(["name", "caption"], row);
        try
        {
            Assert.Equal("IMG_1.jpg, Caption: Sunset", d.PreviewBox.Text);
            d.ShownList.SelectedIndex = 1;
            d.Move(-1);
            Assert.Equal("Caption: Sunset, IMG_1.jpg", d.PreviewBox.Text);
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public void TheListsAndPreview_HaveNames()
    {
        var d = Dialog(["name"]);
        try
        {
            Assert.Equal("Available columns", System.Windows.Automation.AutomationProperties.GetName(d.AvailableList));
            Assert.Equal("Shown columns, in the order they're read", System.Windows.Automation.AutomationProperties.GetName(d.ShownList));
            Assert.Equal("Preview of the selected photo's line", System.Windows.Automation.AutomationProperties.GetName(d.PreviewBox));
            Assert.Equal("There's no photo here to preview.", d.PreviewBox.Text);
        }
        finally { d.Close(); }
    }

    [WpfFact]
    public async Task TheMainWindowsColumns_FollowTheChosenOrder()
    {
        var model = await StartAsync();
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            model.SetColumnOrder(["caption", "name", "rating"]);
            var headers = window.ListColumns.Columns.Select(c => (string)((GridViewColumnHeader)c.Header).Tag).ToList();
            Assert.Equal(["caption", "name", "rating"], headers);
        }
        finally { window.Close(); }
    }
}
