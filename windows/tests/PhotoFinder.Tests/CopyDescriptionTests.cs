using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Edit > Copy Description (#175), as the Mac's #176: the descriptions as text, and what's said.</summary>
public sealed class CopyDescriptionTests
{
    /// <summary>A photo or video row: the kind comes from the extension (".mp4" is a video).</summary>
    private static FileRow Row(string name) =>
        FileRow.ForItem(new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now));

    private static FileRow Folder(string name) => FileRow.ForContainer(new SidebarItem
    {
        Id = SidebarIds.Folder($@"C:\photos\{name}"), Title = name, Kind = SourceKind.Folder, FolderPath = $@"C:\photos\{name}",
        Children = [], ContainerKind = "Folder", Count = 1,
    });

    private static readonly Dictionary<string, string> Saved = new()
    {
        ["a.jpg"] = "A dog on a beach.",
        ["b.jpg"] = "Two cats on a sofa.",
        ["blank.jpg"] = "   ",
    };

    private static (string? Text, string Said) Copy(params FileRow[] rows) =>
        LibraryModel.DescriptionsToCopy(rows, (row, _) => Saved.GetValueOrDefault(row.Name));

    [Fact]
    public void OnePhoto_CopiesItsDescription_AsItIs()
    {
        Assert.Equal(("A dog on a beach.", "Copied the description"), Copy(Row("a.jpg")));
    }

    [Fact]
    public void SeveralPhotos_CopyEachUnderItsName_ABlankLineApart_AndSayWhatsNotDescribed()
    {
        Assert.Equal(("a.jpg\nA dog on a beach.\n\nb.jpg\nTwo cats on a sofa.", "Copied 2 descriptions"), Copy(Row("a.jpg"), Row("b.jpg")));
        Assert.Equal(("a.jpg\nA dog on a beach.\n\nb.jpg\nTwo cats on a sofa.", "Copied 2 descriptions. 1 photo hasn't been described yet"),
            Copy(Row("a.jpg"), Row("c.jpg"), Row("b.jpg")));
        // Several chosen and only one described: still under its name, so the paste says whose it is (as the Mac).
        Assert.Equal(("a.jpg\nA dog on a beach.", "Copied the description. 2 photos haven't been described yet"),
            Copy(Row("a.jpg"), Row("c.jpg"), Row("d.jpg")));
    }

    [Fact]
    public void NothingToCopy_SaysWhy()
    {
        Assert.Equal((null, "Select a photo to copy its description"), Copy());
        Assert.Equal((null, "Videos and folders aren't described"), Copy(Row("clip.mp4")));
        Assert.Equal((null, "Videos and folders aren't described"), Copy(Folder("Holiday"), Row("clip.mp4")));
        Assert.Equal((null, "c.jpg hasn't been described yet"), Copy(Row("c.jpg")));
        // A description that's only spaces is none.
        Assert.Equal((null, "blank.jpg hasn't been described yet"), Copy(Row("blank.jpg")));
        Assert.Equal((null, "None of these photos has been described yet"), Copy(Row("c.jpg"), Row("d.jpg"), Row("clip.mp4")));
        // Videos and folders are left out, not counted as undescribed.
        Assert.Equal(("A dog on a beach.", "Copied the description"), Copy(Row("a.jpg"), Row("clip.mp4"), Folder("Holiday")));
    }

    [Fact]
    public void ASelection_IsCopiedInTheListsOrder_NotTheOrderItWasPicked()
    {
        var rows = new[] { Row("1.jpg"), Row("2.jpg"), Row("3.jpg"), Row("4.jpg"), Row("5.jpg") };
        // Ctrl+Space on 5, then 2, then 3.
        Assert.Equal(["2.jpg", "3.jpg", "5.jpg"], LibraryModel.InListOrder([rows[4], rows[1], rows[2]], rows).Select(r => r.Name));
    }

    [Fact]
    public void ACorrectionNotSavedYet_IsWhatsCopied_ButAnEmptiedBoxIsnt()
    {
        var a = Row("a.jpg");
        Func<FileRow, string?, string?> saved = (row, _) => Saved.GetValueOrDefault(row.Name);
        Assert.Equal(("A dog on a beach, running.", "Copied the description"),
            LibraryModel.DescriptionsToCopy([a], LibraryModel.WithDraft(saved, (a, "A dog on a beach, running."))));
        Assert.Equal(("A dog on a beach.", "Copied the description"), LibraryModel.DescriptionsToCopy([a], LibraryModel.WithDraft(saved, (a, "  "))));
        // Another photo's draft doesn't change this one.
        Assert.Equal(("A dog on a beach.", "Copied the description"), LibraryModel.DescriptionsToCopy([a], LibraryModel.WithDraft(saved, (Row("b.jpg"), "Other"))));
    }

    [Fact]
    public void TheClipboardGetsWindowsLineEndings()
    {
        Assert.Equal("a.jpg\r\nA dog.\r\n\r\nb.jpg\r\nA cat.", LibraryModel.ClipboardText("a.jpg\nA dog.\n\nb.jpg\r\nA cat."));
    }

    [Fact]
    public void ItsCtrlShiftC_InTheEditMenu()
    {
        var gesture = Assert.Single(AppCommands.CopyDescription.InputGestures.OfType<System.Windows.Input.KeyGesture>());
        Assert.Equal((System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), (gesture.Key, gesture.Modifiers));
        Assert.Equal("Copy Descriptio_n", AppCommands.CopyDescription.Text);
    }
}
