using System.Windows;
using System.Windows.Controls;
using PhotoFinder.Models;

namespace PhotoFinder.Views;

/// <summary>
/// View > Labels… (#38), as on the Mac: what's read for each photo, in two tabs of the same editor.
/// - <b>List</b>: which columns the list shows and in what order, which is also the order each row is read in (it was
///   View > Columns).
/// - <b>Gallery</b>: what's said, in order, when you move to a photo in the gallery. Position only by default.
/// It opens on the tab for the view you're in, in its Shown list. OK keeps both tabs; Cancel or Escape leaves them.
/// </summary>
public partial class LabelsDialog : Window
{
    /// <param name="sample">The photo the previews read, with its place in the gallery (counting from 1).</param>
    public LabelsDialog(IEnumerable<string> columns, IEnumerable<string> gallery, FileRow? sample, int number, int count, bool galleryFirst)
    {
        InitializeComponent();
        const string none = "There's no photo here to preview.";
        ListEditor.Set(new OrderChoice(
            Columns.All.Select(c => c.Id).ToList(), id => Columns.Find(id)!.Title, Columns.DefaultOrder, ids => Columns.Clean(ids),
            "column", Columns.Name, $"Columns reset to the default: {Columns.DefaultOrder.Count} shown, Name first.",
            "Choose which columns the list shows, and in what order. Each photo is read in this order too: put Caption or Date Taken first to hear it before the file name.",
            "Available columns", "Shown columns, in the order they're read", "Every column is shown.",
            "Preview of the selected photo's line",
            shown => sample is null ? none : sample.SpokenWith(shown)), Columns.Clean(columns));
        GalleryEditor.Set(new OrderChoice(
            GalleryLabels.All.Select(a => a.Id).ToList(), GalleryLabels.Title, GalleryLabels.DefaultOrder, ids => GalleryLabels.Clean(ids),
            "label", null, "Labels reset to the default: Position.",
            "Choose what's read, and in what order, when you move to a photo in the gallery. The description follows after a pause when Describe Photos Automatically is on.",
            "Available labels", "Shown labels, in the order they're read", "Every label is shown.",
            "Preview of how the selected photo reads in the gallery",
            shown => sample is null ? none : GalleryLabels.Text(sample, number, Math.Max(count, 1), shown)), GalleryLabels.Clean(gallery));
        // Said with the tab, once, rather than with every item in its lists (Kelly, with JAWS).
        System.Windows.Automation.AutomationProperties.SetHelpText(ListTab, ListEditor.Explanation.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(GalleryTab, GalleryEditor.Explanation.Text);
        Tabs.SelectedItem = galleryFirst ? GalleryTab : ListTab;
        Loaded += (_, _) => (galleryFirst ? GalleryEditor : ListEditor).FocusShown();
    }

    /// <summary>The list's shown column ids, in the order they're read.</summary>
    public IReadOnlyList<string> ShownColumns => ListEditor.Shown;

    /// <summary>What the gallery reads, in order.</summary>
    public IReadOnlyList<string> ShownGalleryLabels => GalleryEditor.Shown;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
