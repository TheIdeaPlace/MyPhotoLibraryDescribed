using PhotoFinder.Models;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Edit > Copy Description (#175), as the Mac's #176: the descriptions as text, and what's said.</summary>
public sealed class CopyDescriptionTests
{
    private static FileRow Photo(string name) =>
        FileRow.ForItem(new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now));

    private static FileRow Video(string name) =>
        FileRow.ForItem(new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now));

    private static readonly Dictionary<string, string> Saved = new()
    {
        ["a.jpg"] = "A dog on a beach.",
        ["b.jpg"] = "  Two cats on a sofa.\n",
    };

    private static (string? Text, string Said) Copy(params FileRow[] rows) =>
        LibraryModel.DescriptionsToCopy(rows, (row, _) => Saved.GetValueOrDefault(row.Name));

    [Fact]
    public void OnePhoto_CopiesItsDescription_AsItIs()
    {
        Assert.Equal(("A dog on a beach.", "Copied the description"), Copy(Photo("a.jpg")));
    }

    [Fact]
    public void SeveralPhotos_CopyEachUnderItsName_ABlankLineApart_AndSayWhatsNotDescribed()
    {
        Assert.Equal(("a.jpg\nA dog on a beach.\n\nb.jpg\nTwo cats on a sofa.", "Copied 2 descriptions"), Copy(Photo("a.jpg"), Photo("b.jpg")));
        Assert.Equal(("a.jpg\nA dog on a beach.\n\nb.jpg\nTwo cats on a sofa.", "Copied 2 descriptions. 1 photo hasn't been described yet"),
            Copy(Photo("a.jpg"), Photo("c.jpg"), Photo("b.jpg")));
        Assert.Equal(("A dog on a beach.", "Copied the description. 2 photos haven't been described yet"),
            Copy(Photo("a.jpg"), Photo("c.jpg"), Photo("d.jpg")));
    }

    [Fact]
    public void NothingToCopy_SaysWhy()
    {
        Assert.Equal((null, "Select a photo to copy its description"), Copy());
        Assert.Equal((null, "Videos and folders aren't described"), Copy(Video("clip.mp4")));
        Assert.Equal((null, "c.jpg hasn't been described yet"), Copy(Photo("c.jpg")));
        Assert.Equal((null, "None of these photos has been described yet"), Copy(Photo("c.jpg"), Photo("d.jpg"), Video("clip.mp4")));
        // Videos are left out, not counted as undescribed.
        Assert.Equal(("A dog on a beach.", "Copied the description"), Copy(Photo("a.jpg"), Video("clip.mp4")));
    }

    [Fact]
    public void ItsCtrlShiftC_InTheEditMenu()
    {
        var gesture = Assert.Single(AppCommands.CopyDescription.InputGestures.OfType<System.Windows.Input.KeyGesture>());
        Assert.Equal((System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), (gesture.Key, gesture.Modifiers));
        Assert.Equal("Copy Descriptio_n", AppCommands.CopyDescription.Text);
    }
}
