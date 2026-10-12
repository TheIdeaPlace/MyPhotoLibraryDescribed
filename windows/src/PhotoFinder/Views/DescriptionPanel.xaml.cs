using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Views;

/// <summary>
/// View > Show Description (#50), as on the Mac: the photo you're on, its description in a box you can read by line,
/// word or character and correct (corrections are saved and marked "Edited by you"), a status line ("Describing…",
/// "Saved description", "Edited by you", "Not described yet"), Describe Again, Use as Caption and Copy, and the caption
/// with Edit Caption. Nothing is announced as you arrow; automatic describing still speaks.
/// </summary>
public partial class DescriptionPanel : UserControl
{
    private LibraryModel? _model;
    private Func<AutoDescriber?> _auto = () => null;
    private FileRow? _row;
    /// <summary>The description the box was filled with, so only what the person typed counts as a change.</summary>
    private string _loaded = "";

    public DescriptionPanel() => InitializeComponent();

    /// <summary>Describe Again, Use as Caption and Edit Caption, which the window does.</summary>
    public Action? DescribeAgain { get; set; }
    public Func<FileRow, Task>? UseAsCaption { get; set; }
    public Action? EditCaption { get; set; }

    public void Attach(LibraryModel model, Func<AutoDescriber?> auto)
    {
        _model = model;
        _auto = auto;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryModel.FocusedRow)) Show(model.FocusedRow); };
        model.DescriptionsChanged += _ => Refresh();
        IsVisibleChanged += (_, e) =>
        {
            // Hidden with Ctrl+Shift+I while typing: keep what was typed.
            if (e.NewValue is false) SaveIfChanged();
            else Show(model.FocusedRow);
        };
        Show(model.FocusedRow);
    }

    /// <summary>Called when automatic describing starts or stops on a photo, to say "Describing…".</summary>
    public void DescribingChanged() => UpdateStatus();

    /// <summary>The row the panel shows, for tests.</summary>
    internal FileRow? Row => _row;

    /// <summary>Whether the box holds a change the person typed.</summary>
    internal bool IsDirty => DescriptionBox.Text.Trim() != _loaded.Trim();

    private void Show(FileRow? row)
    {
        // Moving to another photo keeps an unsaved correction rather than dropping it.
        if (row != _row) SaveIfChanged();
        if (_row is not null) _row.PropertyChanged -= Row_PropertyChanged;
        _row = row;
        if (row is not null) row.PropertyChanged += Row_PropertyChanged;
        var describable = row?.Item is { IsVideo: false };
        PhotoPart.Visibility = describable ? Visibility.Visible : Visibility.Collapsed;
        NothingText.Visibility = describable ? Visibility.Collapsed : Visibility.Visible;
        NothingText.Text = row is null ? "Select a photo to see its description." : "Videos and folders aren't described.";
        if (!describable) return;
        NameText.Text = row!.Name;
        CaptionBox.Text = row.Caption.Length > 0 ? row.Caption : "No caption";
        Load();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _row) return;
        // A description arriving (automatic describing, Describe Again) shows at once, even while you're in the box,
        // unless you've started changing it. The caption after Edit Caption or Use as Caption.
        if (e.PropertyName is nameof(FileRow.Description) or "") Refresh();
        if (e.PropertyName is nameof(FileRow.Caption) or "" && _row is { } row) CaptionBox.Text = row.Caption.Length > 0 ? row.Caption : "No caption";
    }

    private void Refresh()
    {
        if (_row?.Item is not { IsVideo: false }) return;
        if (!IsDirty) Load();
        else UpdateStatus();
    }

    private void Load()
    {
        if (_model is null || _row is null) return;
        _loaded = _model.SavedDescription(_row) ?? "";
        DescriptionBox.Text = _loaded;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_model is null || _row?.Item is not { } item) return;
        StatusText.Text = _model.DescriptionStatus(_row, _auto()?.IsDescribing(item.Path) == true);
        var saved = _model.SavedDescription(_row) is not null;
        // Not disabled, so focus never drops off them: pressed without a description, they say why.
        UseAsCaptionButton.Tag = saved;
        CopyButton.Tag = saved;
    }

    /// <summary>Saves what the person typed, if they changed it.</summary>
    internal void SaveIfChanged()
    {
        if (_model is null || _row is null || !IsDirty) return;
        var text = DescriptionBox.Text;
        _model.SaveEditedDescription(_row, text);
        // Emptied: the saved description is kept, and shown again.
        _loaded = _model.SavedDescription(_row) ?? "";
        if (text.Trim().Length == 0) DescriptionBox.Text = _loaded;
        UpdateStatus();
    }

    /// <summary>Where F6 lands: the description box, or the note when there's nothing to describe.</summary>
    public void FocusFirst()
    {
        if (PhotoPart.Visibility == Visibility.Visible) DescriptionBox.Focus();
        else NothingText.Focus();
    }

    private void DescriptionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // A new line, as Shift+Enter does elsewhere.
            var box = DescriptionBox;
            var at = box.CaretIndex;
            box.Text = box.Text.Insert(at, Environment.NewLine);
            box.CaretIndex = at + Environment.NewLine.Length;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (IsDirty) SaveIfChanged();
            e.Handled = true;
        }
    }

    private void DescriptionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveIfChanged();

    private void DescribeAgain_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        DescribeAgain?.Invoke();
    }

    private async void UseAsCaption_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        if (_row is { } row && UseAsCaption is { } use) await use(row);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        if (_model is null || _row is null) return;
        if (_model.SavedDescription(_row) is not { } text)
        {
            Announcer.Announce($"{_row.Name} hasn't been described yet.");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            Announcer.Announce("Description copied");
        }
        catch (ExternalException)
        {
            Announcer.Announce("The clipboard is busy. Try again.");
        }
    }

    private void EditCaption_Click(object sender, RoutedEventArgs e) => EditCaption?.Invoke();
}
