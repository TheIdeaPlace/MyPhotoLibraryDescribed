using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    /// <summary>The Description Type the box was filled with: a correction is saved as that type.</summary>
    private string _loadedKind = "";

    public DescriptionPanel() => InitializeComponent();

    /// <summary>Describe Again, Use as Caption and Edit Caption, which the window does.</summary>
    public Action? DescribeAgain { get; set; }
    public Action<FileRow>? UseAsCaption { get; set; }
    /// <summary>Edit Caption for this photo, with what to focus afterwards.</summary>
    public Action<FileRow, Action>? EditCaption { get; set; }

    public void Attach(LibraryModel model, Func<AutoDescriber?> auto)
    {
        _model = model;
        _auto = auto;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryModel.FocusedRow)) FocusedRowChanged(); };
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

    /// <summary>Whether the box holds something the person typed.</summary>
    internal bool IsDirty => DescriptionBox.Text != _loaded;

    private void FocusedRowChanged()
    {
        if (_model is null) return;
        // A reload passes through no selection for a moment. While you're in the panel, look again once it settles
        // rather than treating that as moving away (which would save what's half typed).
        if (_model.FocusedRow is null && IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(() => Show(_model.FocusedRow), DispatcherPriority.Background);
            return;
        }
        Show(_model.FocusedRow);
    }

    private void Show(FileRow? row)
    {
        // The same photo again (a re-sort, or a reload with new row objects): follow the new row, keep what's typed.
        if (row is not null && _row is not null && row.Id == _row.Id)
        {
            if (row != _row)
            {
                _row.PropertyChanged -= Row_PropertyChanged;
                _row = row;
                row.PropertyChanged += Row_PropertyChanged;
            }
            UpdateStatus();
            return;
        }
        // Moving to another photo keeps an unsaved correction rather than dropping it.
        SaveIfChanged();
        if (_row is not null) _row.PropertyChanged -= Row_PropertyChanged;
        _row = row;
        if (row is not null) row.PropertyChanged += Row_PropertyChanged;
        var describable = row?.Item is { IsVideo: false };
        PhotoPart.Visibility = describable ? Visibility.Visible : Visibility.Collapsed;
        NothingText.Visibility = describable ? Visibility.Collapsed : Visibility.Visible;
        NothingText.Text = row is null ? "Select a photo to see its description." : "Videos and folders aren't described.";
        if (!describable) return;
        NameText.Text = row!.Name;
        ShowCaption(row);
        Load();
    }

    private void ShowCaption(FileRow row) => CaptionBox.Text = row.Caption.Length > 0 ? row.Caption : "No caption";

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _row || _row is null) return;
        // A description arriving (automatic describing, Describe Again) shows at once, even while you're in the box,
        // unless you've started changing it. The caption after Edit Caption or Use as Caption.
        if (e.PropertyName is nameof(FileRow.Description) or "") Refresh();
        if (e.PropertyName is nameof(FileRow.Caption) or "") ShowCaption(_row);
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
        _loadedKind = _model.Settings.DescriptionKind;
        _loaded = _model.SavedDescription(_row) ?? "";
        DescriptionBox.Text = _loaded;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_model is null || _row?.Item is not { } item) return;
        StatusText.Text = _model.DescriptionStatus(_row, _auto()?.IsDescribing(item.Path) == true);
        // Heard with the box (F6 lands there): which photo, and where its description stands.
        AutomationProperties.SetHelpText(DescriptionBox, $"{_row.Name}. {StatusText.Text}. Enter saves a correction; Shift+Enter starts a new line; Escape undoes it.");
    }

    /// <summary>Saves what the person typed, if they changed it, as the Description Type it was loaded as.</summary>
    internal void SaveIfChanged()
    {
        if (_model is null || _row is null || !IsDirty) return;
        var text = DescriptionBox.Text;
        _model.SaveEditedDescription(_row, text, _loadedKind);
        // Emptied: the saved description is kept, and shown again.
        _loaded = _model.SavedDescription(_row, _loadedKind) ?? "";
        if (text.Trim().Length == 0 || text.Trim() == _loaded) DescriptionBox.Text = _loaded;
        else _loaded = text;
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
        // Shift+Enter is left to the box, which starts a new line itself (AcceptsReturn).
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            SaveIfChanged();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && IsDirty)
        {
            DescriptionBox.Text = _loaded;
            DescriptionBox.CaretIndex = DescriptionBox.Text.Length;
            Announcer.Announce("Correction undone");
            e.Handled = true;
        }
    }

    private void DescriptionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Its own right-click menu (Paste, say) isn't leaving the box.
        if (e.NewFocus is DependencyObject d && InContextMenu(d)) return;
        SaveIfChanged();
    }

    private static bool InContextMenu(DependencyObject d)
    {
        for (DependencyObject? p = d; p is not null; p = LogicalTreeHelper.GetParent(p) ?? (p as FrameworkElement)?.TemplatedParent)
            if (p is ContextMenu) return true;
        return false;
    }

    private void DescribeAgain_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        DescribeAgain?.Invoke();
    }

    private void UseAsCaption_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        if (_row is { } row) UseAsCaption?.Invoke(row);
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

    private void EditCaption_Click(object sender, RoutedEventArgs e)
    {
        SaveIfChanged();
        if (_row is { } row && sender is Button button) EditCaption?.Invoke(row, () => button.Focus());
    }
}
