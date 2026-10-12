using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Views;

public partial class MainWindow : Window
{
    private readonly LibraryModel _model;
    private readonly ThumbnailCache _thumbnails = new();
    private readonly Dictionary<string, Task<ImageSource?>> _galleryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _galleryOrder = new();
    private bool _applyingSelection;
    private bool _sidebarHadFocus;
    private Point _dragStart;
    private bool _dragArmed;
    private string? _galleryShownId;
    private FileRow? _galleryRow;

    /// <summary>Speaks Windows AI descriptions of photos as they're arrowed to. Null where there's no helper (tests).</summary>
    public AutoDescriber? AutoDescriber { get; init; }

    /// <summary>Describes the photos in added folders behind the scenes. Null where there's no helper (tests).</summary>
    public BackgroundDescriber? BackgroundDescriber { get; init; }

    private static readonly Dictionary<string, string> ColumnProperties = new()
    {
        ["date"] = nameof(FileRow.DateText),
        ["kind"] = nameof(FileRow.Kind),
        ["caption"] = nameof(FileRow.Caption),
        ["dimensions"] = nameof(FileRow.Dimensions),
        ["duration"] = nameof(FileRow.DurationText),
        ["favorite"] = nameof(FileRow.FavoriteText),
        ["keywords"] = nameof(FileRow.Keywords),
        ["rating"] = nameof(FileRow.RatingText),
        ["modified"] = nameof(FileRow.ModifiedText),
        ["fileType"] = nameof(FileRow.FileType),
        ["size"] = nameof(FileRow.SizeText),
        ["orientation"] = nameof(FileRow.Orientation),
        ["megapixels"] = nameof(FileRow.MegapixelsText),
        ["location"] = nameof(FileRow.LocationText),
        ["folder"] = nameof(FileRow.Folder),
        ["description"] = nameof(FileRow.Description),
    };

    public MainWindow(LibraryModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        BindCommands();
        BuildColumns();
        BuildSortMenu();
        BuildKindMenu();
        SaveInPhotosItem.IsChecked = model.Settings.SaveDescriptionsInPhotos;
        ReadOnlyItem.IsChecked = model.IsReadOnly;
        UpdateTitle();
        SetUpDescriptionPanel();
        // WPF presses a button by its access key on a plain letter when focus is somewhere that doesn't take typing (the
        // gallery photo, the sidebar, the status bar): "c" in the gallery would run Use as Caption and change the file.
        // A letter no control used is dropped here; Alt+letter arrives differently and still works.
        AddHandler(TextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Text.Length > 0 && !char.IsControl(e.Text[0])) e.Handled = true;
        }));

        model.PropertyChanged += Model_PropertyChanged;
        model.SelectionRequested += Model_SelectionRequested;
        model.ErrorRaised += message => ShowError(message);
        model.ColumnsChanged += BuildColumns;
        model.SidebarRebuilding += () => _sidebarHadFocus = Sidebar.IsKeyboardFocusWithin;
        model.SidebarRebuilt += () => { if (_sidebarHadFocus) Dispatcher.BeginInvoke(FocusSidebarSelection, System.Windows.Threading.DispatcherPriority.Loaded); };

        Loaded += async (_, _) =>
        {
            UpdateTitle();
            ApplyViewMode();
            FileList.Focus();
            try
            {
                await model.StartAsync();
            }
            catch (Exception ex)
            {
                ShowError($"{AppInfo.Name} couldn't read your library: {ex.Message}");
            }
            UpdateEmptyText();
            FocusContent();
        };
        model.FilesChanged += paths => { foreach (var p in paths) _thumbnails.Forget(p); };
        Closing += async (_, e) =>
        {
            // A caption being written from a description is finished first, so no file is left half written.
            if (model.IsWritingCaptions && !_closingAfterCaptions)
            {
                e.Cancel = true;
                _closingAfterCaptions = true;
                await model.StopCaptionsAsync();
                Close();
                return;
            }
            if (model.IsWriting && MessageBox.Show(this,
                    $"{AppInfo.Name} is still saving changes to your photos. If you quit now, the rest won't be changed. Quit anyway?",
                    "Still Saving", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            AutoDescriber?.Dispose();
            BackgroundDescriber?.Dispose();
            model.Dispose();
        };
    }

    // MARK: Commands

    private void BindCommands()
    {
        void Bind(ICommand command, Action execute, Func<bool>? canExecute = null) =>
            CommandBindings.Add(new CommandBinding(command, (_, e) => { execute(); e.Handled = true; },
                (_, e) => { e.CanExecute = canExecute?.Invoke() ?? true; e.Handled = true; }));

        bool HasFiles() => _model.SelectedRows.Any(r => !r.IsContainer);
        bool HasSelection() => _model.SelectedRows.Count > 0;

        Bind(AppCommands.OpenItem, () => _model.Open(), HasSelection);
        Bind(AppCommands.OpenSelected, () => _model.Open(), HasSelection);
        Bind(AppCommands.Preview, ShowPreview, HasFiles);
        Bind(AppCommands.GetInfo, ShowInfo, HasFiles);
        // Available on any row, so the shortcut on a folder or video says why it can't, rather than nothing.
        Bind(AppCommands.DescribePhoto, () => DescribePhoto(again: false), () => _model.FocusedRow is not null);
        // Not while typing: Ctrl+Alt+Shift+D is AltGr+Shift+D, which types a character on some keyboard layouts (Ð on
        // US-International), and the search box should get it.
        Bind(AppCommands.DescribeAgain, () => DescribePhoto(again: true), () => _model.FocusedRow is not null && !IsTyping());
        Bind(AppCommands.ShowInExplorer, () => _model.ShowInExplorer(), () => _model.SelectedRows.Any(r => r.Item is not null || r.Container?.FolderPath is not null));
        Bind(AppCommands.Export, ExportSelection, HasFiles);
        Bind(AppCommands.AddFolder, AddFolder);
        Bind(AppCommands.RemoveFolder, RemoveFolder, () => _model.CurrentRoot is not null);
        Bind(AppCommands.Refresh, () => _model.Fire(_model.RescanAsync()), () => !_model.IsLoading);
        Bind(AppCommands.Exit, Close);
        Bind(ApplicationCommands.Copy, () => _model.Copy(), HasFiles);
        Bind(ApplicationCommands.SelectAll, () => { if (_model.ViewMode == ViewMode.List) { FileList.SelectAll(); } });
        Bind(AppCommands.Find, () => { SearchBox.Focus(); SearchBox.SelectAll(); });
        Bind(AppCommands.AsList, () => SetViewMode(ViewMode.List));
        Bind(AppCommands.AsGallery, () => SetViewMode(ViewMode.Gallery));
        Bind(AppCommands.EnclosingFolder, _model.GoToEnclosingFolder, () => _model.CurrentSource?.ParentId is not null);
        Bind(AppCommands.AllPhotos, () => _model.Navigate(SidebarIds.AllPhotos));
        Bind(AppCommands.NextPane, () => MovePane(1));
        Bind(AppCommands.PreviousPane, () => MovePane(-1));
        Bind(AppCommands.ToggleFavorite, () => { if (CanChangePhotos() && (_model.IsWriting || ConfirmBulkWrite("Toggle Favorite"))) _model.Fire(_model.ToggleFavoriteAsync()); }, HasFiles);
        Bind(AppCommands.EditCaption, () => EditCaption(), HasFiles);
        Bind(AppCommands.ReadStatusBar, ReadStatusBar);
        Bind(AppCommands.GoToStatusBar, FocusStatusBar);
        Bind(AppCommands.UseDescriptionAsCaption, () => _model.Fire(UseDescriptionAsCaptionAsync(_model.SelectedRows)), HasFiles);
        Bind(AppCommands.About, ShowAbout);
        Bind(AppCommands.CheckForUpdates, CheckForUpdates);
        Bind(AppCommands.CheckSystem, ShowCheckSystem);
        Bind(AppCommands.ReportBug, () => ReportBug(null));
        Bind(AppCommands.ChooseColumns, ChooseColumns);
        Bind(AppCommands.ShowDescription, () => ShowDescriptionPanel(!_model.Settings.ShowDescriptionPanel, announce: true));
        Bind(AppCommands.AutoDescribe, () =>
        {
            if (AutoDescriber is null) return;
            AutoDescriber.Enabled = !AutoDescriber.Enabled;
            AutoDescribeItem.IsChecked = AutoDescriber.Enabled;
        }, () => AutoDescriber is not null);
        Bind(AppCommands.DescribeInBackground, () =>
        {
            if (BackgroundDescriber is null) return;
            BackgroundDescriber.Enabled = !BackgroundDescriber.Enabled;
            BackgroundDescribeItem.IsChecked = BackgroundDescriber.Enabled;
        }, () => BackgroundDescriber is not null);
        Bind(AppCommands.StopBackground, () => BackgroundDescriber?.Stop(), () => BackgroundDescriber?.IsRunning == true);
        Loaded += (_, _) =>
        {
            BackgroundDescribeItem.IsChecked = BackgroundDescriber?.Enabled ?? false;
            AutoDescribeItem.IsChecked = AutoDescriber?.Enabled ?? false;
            if (AutoDescriber is null) return;
            // Descriptions are for the photo you're on: the list or gallery has focus, and this window is in front
            // (not the sidebar, search, Get Info or a message).
            AutoDescriber.CanSpeak = () => IsActive && (FileList.IsKeyboardFocusWithin || GalleryImage.IsKeyboardFocusWithin);
            FileList.GotKeyboardFocus += (_, _) => AutoDescriber.Refocused();
            GalleryImage.GotKeyboardFocus += (_, _) => AutoDescriber.Refocused();
        };
        CommandBindings.Add(new CommandBinding(AppCommands.Rate,
            (_, e) =>
            {
                if (int.TryParse(e.Parameter?.ToString(), out var stars) && CanChangePhotos() && (_model.IsWriting || ConfirmBulkWrite("Set Rating"))) _model.Fire(_model.SetRatingAsync(stars));
                e.Handled = true;
            },
            (_, e) => { e.CanExecute = HasFiles(); e.Handled = true; }));
    }

    // MARK: Columns

    /// <summary>Widths people have dragged columns to, kept when columns are turned on and off.</summary>
    private readonly Dictionary<string, double> _columnWidths = [];

    private void BuildColumns()
    {
        foreach (var existing in ListColumns.Columns)
            if (existing.Header is GridViewColumnHeader { Tag: string id }) _columnWidths[id] = existing.ActualWidth > 0 ? existing.ActualWidth : existing.Width;
        ListColumns.Columns.Clear();
        foreach (var column in _model.ShownColumns)
        {
            var gridColumn = new GridViewColumn
            {
                Header = MakeHeader(column),
                Width = _columnWidths.GetValueOrDefault(column.Id, column.Width),
            };
            if (column.Id == Columns.Name) gridColumn.CellTemplate = (DataTemplate)FindResource("NameCellTemplate");
            else gridColumn.DisplayMemberBinding = new Binding(ColumnProperties[column.Id]);
            ListColumns.Columns.Add(gridColumn);
        }
    }

    /// <summary>Shows which column is sorted, without rebuilding the columns (which would lose their widths).</summary>
    private void UpdateSortHeaders()
    {
        foreach (var column in ListColumns.Columns)
        {
            if (column.Header is not GridViewColumnHeader { Tag: string id } header || Columns.Find(id) is not { } info) continue;
            var sorted = _model.SortColumn == id;
            header.Content = sorted ? $"{info.Title} {(_model.SortDescending ? "▼" : "▲")}" : info.Title;
            AutomationProperties.SetItemStatus(header, sorted ? (_model.SortDescending ? "sorted descending" : "sorted ascending") : "");
        }
    }

    private GridViewColumnHeader MakeHeader(ColumnInfo column)
    {
        var sorted = _model.SortColumn == column.Id;
        var header = new GridViewColumnHeader
        {
            Tag = column.Id,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2, 6, 2),
            Content = sorted ? $"{column.Title} {(_model.SortDescending ? "▼" : "▲")}" : column.Title,
            ContextMenu = MakeColumnsContextMenu(),
        };
        // The arrow is for sighted people; screen readers hear the title and the sort as words.
        AutomationProperties.SetName(header, column.Title);
        if (sorted) AutomationProperties.SetItemStatus(header, _model.SortDescending ? "sorted descending" : "sorted ascending");
        header.Click += (_, _) => _model.SortBy(column.Id);
        return header;
    }

    /// <summary>Right-clicking a column header opens the Columns dialog, the one place columns are chosen and ordered.</summary>
    private ContextMenu MakeColumnsContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Command = AppCommands.ChooseColumns, CommandTarget = this });
        return menu;
    }

    /// <summary>View, Columns: which columns, and in what order they're shown and read.</summary>
    private void ChooseColumns()
    {
        var sample = _model.FocusedRow is { IsContainer: false } focused ? focused : _model.Rows.FirstOrDefault(r => !r.IsContainer);
        var before = _model.ShownColumns.Select(c => c.Id).ToList();
        var returnTo = Keyboard.FocusedElement as IInputElement;
        var dialog = new ColumnsDialog(before, sample) { Owner = this };
        // The new order first, then focus back: the focused row reading in its new order is the confirmation,
        // rather than the old order, a name change and an announcement all at once.
        if (dialog.ShowDialog() == true && !dialog.ShownColumns.SequenceEqual(before)) _model.SetColumnOrder(dialog.ShownColumns);
        // Back where it was (it can be opened from the sidebar or search); the list if that's gone.
        if (returnTo is UIElement { IsVisible: true } element && element.Focus()) return;
        FocusContent();
    }

    private void BuildSortMenu()
    {
        SortMenu.Items.Clear();
        foreach (var column in Columns.All)
        {
            var item = new AppMenuItem { Header = column.Title, IsCheckable = true, IsChecked = _model.SortColumn == column.Id, Tag = column.Id };
            item.Click += (_, _) => _model.SortBy(column.Id);
            SortMenu.Items.Add(item);
        }
    }

    /// <summary>Photo > Description Type: what Describe Photo and automatic descriptions ask Windows for. Get Info's
    /// choice changes it too.</summary>
    private void BuildKindMenu()
    {
        foreach (var (id, title) in Services.WindowsAI.Kinds)
        {
            // Detailed and Diagram share a first letter, so Diagram uses G.
            var header = id == "diagram" ? "Dia_gram" : $"_{title}";
            var item = new AppMenuItem { Header = header, IsCheckable = true, Tag = id };
            item.Click += (_, _) => SetDescriptionKind(id, title);
            KindMenu.Items.Add(item);
        }
        KindMenu.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in KindMenu.Items) item.IsChecked = (string)item.Tag == _model.Settings.DescriptionKind;
        };
    }

    /// <summary>Photo > Save Descriptions in Photos (#62, Kelly's 12 Oct direction): one switch, off until the person agrees
    /// once. Asking is the permission to change their photos; when on, only empty captions and alt text are filled.</summary>
    private void SaveInPhotos_Click(object sender, RoutedEventArgs e) => SetSaveInPhotos(!_model.Settings.SaveDescriptionsInPhotos);

    /// <summary>"caption", "caption and alt text", "caption, alt text and extended description".</summary>
    private static string Joined(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    internal void SetSaveInPhotos(bool on)
    {
        // While read-only, turning it on would promise something that can't happen.
        if (on && !CanChangePhotos())
        {
            SaveInPhotosItem.IsChecked = false;
            return;
        }
        if (on && !QuestionDialog.Ask(ActiveWindow, "Save Descriptions in Photos", "Save descriptions in your photos?",
                "From now on, each new description is also saved in the photo: as its caption when it has none, and in the file's accessibility fields. Captions and alt text that you or other apps wrote are never replaced. You can turn this off at any time.",
                "Save in Photos"))
        {
            SaveInPhotosItem.IsChecked = false;
            return;
        }
        _model.Settings.SaveDescriptionsInPhotos = on;
        _model.SaveSettings();
        SaveInPhotosItem.IsChecked = on;
        // Queued behind the menu closing and focus returning to the list, so it isn't cut off.
        Services.Announcer.AnnounceAfter(on ? "New descriptions are saved in your photos" : "Descriptions aren't saved in your photos", "PhotoFinderSetting");
    }

    /// <summary>Photo > Use Description as Caption: each chosen photo's saved description becomes its caption, and for JPEG
    /// and TIFF files its alt text too (the accessibility fields, #62). What's in the files is read first, never the list's
    /// copy (empty until details load), so a caption or alt text someone wrote is never replaced without asking.</summary>
    /// <param name="owner">The window asking: Get Info, or this one.</param>
    /// <param name="kind">The description type to use (Get Info's choice); otherwise Photo > Description Type.</param>
    internal async Task UseDescriptionAsCaptionAsync(IReadOnlyList<FileRow> rows, Window? owner = null, string? kind = null)
    {
        owner ??= this;
        if (!CanChangePhotos()) return;
        var photos = rows.Where(r => r.Item is { IsVideo: false }).ToList();
        if (photos.Count == 0)
        {
            Services.Announcer.Announce("Select a photo");
            return;
        }
        kind ??= _model.Settings.DescriptionKind;
        var described = photos
            .Select(r => (Row: r, Text: _model.Descriptions?.Get(r.Item!, kind)))
            .Where(d => !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => (d.Row, Text: d.Text!.Trim()))
            .ToList();
        if (described.Count == 0)
        {
            Services.Announcer.Announce(NotDescribedMessage(photos, kind));
            return;
        }
        if (!ConfirmBulkWrite("Use Description as Caption", described.Select(d => d.Row.Item!).ToList(), owner)) return;
        // Each caption as the file has it now. One that can't be read (locked, moved) is left alone.
        var current = await Task.Run(async () =>
        {
            var list = new List<(FileRow Row, string Text, string Caption, string AltText, string Extended)>();
            foreach (var (row, text) in described)
            {
                try
                {
                    // A photo whose fields can't be read now is counted as unreadable, not as having none.
                    var now = await MetadataReader.ReadAsync(row.Item!.Path, forWriting: true);
                    list.Add((row, text, now.Caption.Trim(), now.AltText.Trim(), now.ExtendedDescription.Trim()));
                }
                catch (Exception)
                {
                }
            }
            return list;
        });
        var unreadable = described.Count - current.Count;
        if (current.Count == 0)
        {
            ShowError("The captions couldn't be read, so they weren't changed.", owner);
            return;
        }
        // The Mac's question: "Replace the caption and alt text of name?" with what each holds, or, for several photos,
        // "Replace what's already in N photos?".
        var replacing = current
            .SelectMany(c => (XmpFields.CanWrite(c.Row.Item!.Path) ? FileFields.Replaced(c.Caption, c.AltText, c.Extended, c.Text) : FileFields.Replaced(c.Caption, "", "", c.Text))
                .Select(f => (c.Row.Name, f.Field, f.Current)))
            .ToList();
        if (replacing.Count > 0)
        {
            var names = replacing.Select(r => r.Name).Distinct().ToList();
            var question = names.Count == 1
                ? $"Replace the {Joined(replacing.Select(r => r.Field).ToList())} of {names[0]}?\n\n" + string.Join("\n", replacing.Select(r => $"Its {r.Field} is: {r.Current}"))
                : $"Replace what's already in {names.Count} photos?\n\nTheir captions, alt text or extended descriptions will be replaced with their descriptions.";
            if (MessageBox.Show(owner, question, "Use Description as Caption", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        // Already holding it (caption, and alt text where the file has one): nothing to write, but it counts as saved.
        var toWrite = current
            .Where(c => c.Caption != c.Text || (XmpFields.CanWrite(c.Row.Item!.Path)
                && (c.AltText != FileFields.AltText(c.Text) || c.Extended != (FileFields.ExtendedDescription(c.Text) ?? ""))))
            .Select(c => (c.Row.Item!, c.Text)).ToList();
        await _model.SaveCaptionsAsync(toWrite, current.Count - toWrite.Count, photos.Count - described.Count);
        if (unreadable > 0)
            ShowError(unreadable == 1 ? "1 caption couldn't be read, so it wasn't changed." : $"{unreadable} captions couldn't be read, so they weren't changed.", owner);
    }

    /// <summary>Why there's nothing to use: not described at all, or only as another Description Type.</summary>
    private string NotDescribedMessage(IReadOnlyList<FileRow> photos, string kind)
    {
        var otherKind = photos.Any(p => Services.WindowsAI.Kinds.Any(k => k.Id != kind && _model.Descriptions?.Get(p.Item!, k.Id) is not null));
        var title = Services.WindowsAI.Kinds.FirstOrDefault(k => k.Id == kind).Title ?? kind;
        if (photos.Count == 1)
            return otherKind ? $"{photos[0].Name} hasn't been described as {title} yet. Describe it first." : $"{photos[0].Name} hasn't been described yet. Describe it first.";
        return otherKind ? $"None of these photos have been described as {title} yet." : "None of these photos have been described yet.";
    }

    internal void SetDescriptionKind(string id, string title)
    {
        // A correction being typed is saved as the type it was typed for.
        DescriptionPanel.SaveIfChanged();
        _model.Settings.DescriptionKind = id;
        _model.SaveSettings();
        _model.DescriptionKindChanged();
        Services.Announcer.Announce($"Photos get {title} descriptions");
    }

    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        ShowDescriptionItem.IsCheckable = true;
        ShowDescriptionItem.IsChecked = _model.Settings.ShowDescriptionPanel;
        AsListItem.IsCheckable = true;
        AsListItem.IsChecked = _model.ViewMode == ViewMode.List;
        AsGalleryItem.IsCheckable = true;
        AsGalleryItem.IsChecked = _model.ViewMode == ViewMode.Gallery;
        foreach (MenuItem item in SortMenu.Items)
        {
            var sorted = (string)item.Tag == _model.SortColumn;
            item.IsChecked = sorted;
            var title = Columns.Find((string)item.Tag)!.Title;
            item.Header = sorted ? $"{title} ({(_model.SortDescending ? "descending" : "ascending")})" : title;
        }
    }

    // MARK: Model changes

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryModel.Title) or nameof(LibraryModel.IsReadOnly):
                UpdateTitle();
                ReadOnlyItem.IsChecked = _model.IsReadOnly;
                break;
            case nameof(LibraryModel.Rows):
                UpdateEmptyText();
                break;
            case nameof(LibraryModel.SortColumn) or nameof(LibraryModel.SortDescending):
                UpdateSortHeaders();
                break;
            case nameof(LibraryModel.FocusedRow):
                if (_model.ViewMode == ViewMode.Gallery) UpdateGallery();
                break;
            case nameof(LibraryModel.IsLoading):
                UpdateEmptyText();
                break;
        }
    }

    /// <summary>"Pictures - Photo Finder - Read-Only", or "- Editing" once changes to photos are allowed (#83), so it's
    /// heard with the window's name.</summary>
    private void UpdateTitle()
    {
        var mode = _model.IsReadOnly ? "Read-Only" : "Editing";
        Title = _model.CurrentSource is null ? $"{AppInfo.Name} - {mode}" : $"{_model.Title} - {AppInfo.Name} - {mode}";
    }

    /// <summary>Before a command that would change a photo: while read-only, says why it can't and says no.</summary>
    internal bool CanChangePhotos()
    {
        if (!_model.IsReadOnly) return true;
        System.Media.SystemSounds.Beep.Play();
        Services.Announcer.Announce(Services.PhotoChanges.ReadOnlyMessage);
        return false;
    }

    /// <summary>File > Read-Only. Turning it off asks every time, with the risk said plainly; turning it on doesn't.</summary>
    private void ReadOnly_Click(object sender, RoutedEventArgs e) => SetReadOnly(!_model.IsReadOnly);

    internal void SetReadOnly(bool on)
    {
        if (!on && !QuestionDialog.Ask(ActiveWindow, "Allow Changes to Photos", "Let Photo Finder change your photos?",
                "Photo Finder will be able to write captions, alt text and ratings into your photo files, and Save Descriptions in Photos will work. Every change is checked before it's saved, but an undiscovered bug could still damage a photo, so keep a backup of photos you care about. You can go back to read-only at any time.",
                "Allow Changes"))
        {
            ReadOnlyItem.IsChecked = true;
            return;
        }
        _model.IsReadOnly = on;
        ReadOnlyItem.IsChecked = on;
        // Queued behind the menu closing and focus returning, so it isn't cut off.
        Services.Announcer.AnnounceAfter(on ? "Read-only. Photos won't be changed" : "Editing. Photo Finder can change your photos", "PhotoFinderSetting");
    }

    private void UpdateEmptyText()
    {
        var empty = _model.Rows.Count == 0;
        EmptyText.Text = _model.IsLoading ? "Loading…"
            : _model.SearchText.Trim().Length > 0 ? $"No results for “{_model.SearchText.Trim()}”"
            : "There is nothing here.";
        EmptyText.Visibility = empty && _model.ViewMode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        if (_model.ViewMode == ViewMode.Gallery) UpdateGallery();
    }

    private void Model_SelectionRequested(IReadOnlyList<string> ids, bool focus)
    {
        var listHadFocus = FileList.IsKeyboardFocusWithin;
        var wanted = new HashSet<string>(ids);
        var rows = _model.Rows.Where(r => wanted.Contains(r.Id)).ToList();
        _applyingSelection = true;
        try
        {
            FileList.SelectOnly(rows);
        }
        finally
        {
            _applyingSelection = false;
        }
        _model.SelectedRows = rows;
        if (rows.Count > 0) FileList.ScrollIntoView(rows[0]);
        if (_model.ViewMode == ViewMode.List && (focus || listHadFocus)) FocusListRow(rows.FirstOrDefault());
    }

    /// <summary>Puts keyboard focus on a row (or the list), once its container exists.</summary>
    private void FocusListRow(FileRow? row)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var target = row ?? (FileList.SelectedItem as FileRow);
            if (target is not null)
            {
                FileList.ScrollIntoView(target);
                FileList.UpdateLayout();
                if (FileList.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem container)
                {
                    container.Focus();
                    return;
                }
            }
            if (FileList.Items.Count > 0 && FileList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem first)
                first.Focus();
            else
                FileList.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusContent()
    {
        if (_model.ViewMode == ViewMode.Gallery) GalleryImage.Focus();
        else FocusListRow(_model.FocusedRow);
    }

    private void FocusSidebarSelection()
    {
        if (_model.CurrentSource is { } item && FindTreeItem(Sidebar, item) is { } container) container.Focus();
        else Sidebar.Focus();
    }

    private static TreeViewItem? FindTreeItem(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct) return direct;
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem { IsExpanded: true } container
                && FindTreeItem(container, item) is { } found)
                return found;
        }
        return null;
    }

    // MARK: Sidebar

    private void Sidebar_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SidebarItem item) _model.SidebarSelected(item);
    }

    private void Sidebar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Into the list, as Finder's sidebar does.
            FocusContent();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (SidebarRemovalProblem() is { } problem)
            {
                System.Media.SystemSounds.Beep.Play();
                Services.Announcer.Announce(problem);
            }
            else
            {
                RemoveFolder();
            }
            e.Handled = true;
        }
    }

    /// <summary>Why Delete can't remove the sidebar item you're on, or null when it's a library folder it can remove
    /// (after asking). Only whole library folders come out; a folder inside one stays with it.</summary>
    internal string? SidebarRemovalProblem()
    {
        if (_model.CurrentRoot is not { } root) return "Only folders in the library can be removed.";
        if (!string.Equals(_model.CurrentSource?.FolderPath, root, StringComparison.OrdinalIgnoreCase))
            return $"This folder is part of {Path.GetFileName(root)}. Only whole library folders can be removed.";
        return null;
    }

    // MARK: List

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSelection) return;
        _model.SelectedRows = FileList.SelectedItems.Cast<FileRow>().ToList();
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                // Focus can sit on a row with nothing selected (a screen reader moving it, say); Enter then
                // opens the focused row rather than doing nothing.
                if (_model.SelectedRows.Count == 0 && (e.OriginalSource as FrameworkElement)?.DataContext is FileRow focused)
                    _model.Open([focused]);
                else
                    _model.Open();
                e.Handled = true;
                break;
            case Key.Space:
                ShowPreview();
                e.Handled = true;
                break;
            case Key.Back:
                _model.GoToEnclosingFolder();
                e.Handled = true;
                break;
        }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem) _model.Open();
    }

    private void NameCell_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // A recycled cell lets go of its old row's picture, so only rows on screen hold thumbnails;
        // the cache keeps recent ones for scrolling back.
        if (e.OldValue is FileRow old) old.Thumbnail = null;
        RequestThumbnail((FrameworkElement)sender, e.NewValue as FileRow);
    }

    private void NameCell_Loaded(object sender, RoutedEventArgs e) =>
        RequestThumbnail((FrameworkElement)sender, ((FrameworkElement)sender).DataContext as FileRow);

    private void RequestThumbnail(FrameworkElement cell, FileRow? row)
    {
        if (row?.Item is not { } item || row.Thumbnail is not null) return;
        _thumbnails.Request(item.Path, image => { if (cell.DataContext == row) row.Thumbnail = image; },
            stillWanted: () => cell.DataContext == row && cell.IsVisible);
    }

    // Dragging rows out drops the original files, into File Explorer, Mail and so on.
    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragArmed = ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(FileList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var paths = _model.SelectedRows.Select(r => r.Item?.Path).OfType<string>().ToArray();
        if (paths.Length == 0) return;
        DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }

    // MARK: Search

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            _model.SearchText = "";
            FocusContent();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            // Into the results.
            _model.SearchText = SearchBox.Text;
            if (_model.FocusedRow is null && _model.Rows.Count > 0) Model_SelectionRequested([_model.Rows[0].Id], false);
            FocusContent();
            e.Handled = true;
        }
    }

    // MARK: Panes

    /// <summary>F6 and Shift+F6 move between the sidebar, the list or gallery, search and the status bar.</summary>
    private void MovePane(int direction)
    {
        // Sidebar, the list or gallery, the Description panel when it's showing, search, then the status bar.
        var panes = new List<(Action Focus, Func<bool> Has)>
        {
            (FocusSidebarSelection, () => Sidebar.IsKeyboardFocusWithin),
            (FocusContent, () => false),
        };
        if (DescriptionPanel.IsVisible && DescriptionPanel.ActualWidth > 0) panes.Add((DescriptionPanel.FocusFirst, () => DescriptionPanel.IsKeyboardFocusWithin));
        panes.Add((() => SearchBox.Focus(), () => SearchBox.IsKeyboardFocusWithin));
        panes.Add((FocusStatusBar, () => MainStatusBar.IsKeyboardFocusWithin));
        var current = panes.FindIndex(p => p.Has());
        if (current < 0) current = 1;
        panes[(current + direction + panes.Count) % panes.Count].Focus();
    }

    // MARK: Description panel (#50)

    private void SetUpDescriptionPanel()
    {
        DescriptionPanel.Attach(_model, () => AutoDescriber);
        DescriptionPanel.DescribeAgain = () => DescribePhoto(again: true);
        DescriptionPanel.UseAsCaption = row => _model.Fire(UseDescriptionAsCaptionAsync([row]));
        // The panel's photo only, and back to its Edit Caption button afterwards.
        DescriptionPanel.EditCaption = (row, refocus) => EditCaption([row], refocus);
        Loaded += (_, _) => { if (AutoDescriber is not null) AutoDescriber.DescribingChanged += DescriptionPanel.DescribingChanged; };
        ShowDescriptionPanel(_model.Settings.ShowDescriptionPanel, announce: false, save: false);
        Closing += (_, _) =>
        {
            // Typed in the box, then closed the window: keep it.
            DescriptionPanel.SaveIfChanged();
            if (DescriptionPanel.IsVisible) RememberPanelWidth();
        };
    }

    /// <summary>View > Show Description (Ctrl+Shift+I). The gallery shows the description under the photo only while the
    /// panel is hidden, so it isn't shown twice.</summary>
    internal void ShowDescriptionPanel(bool show, bool announce, bool save = true)
    {
        if (!show && DescriptionPanel.IsVisible)
        {
            RememberPanelWidth();
            // Leaving the panel while focus is in it: back to the list or gallery rather than nowhere.
            if (DescriptionPanel.IsKeyboardFocusWithin) FocusContent();
        }
        _model.Settings.ShowDescriptionPanel = show;
        if (save) _model.SaveSettings();
        DescriptionPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter.Visibility = DescriptionPanel.Visibility;
        // A minimum while it's showing, so the splitter can't squeeze it to nothing that F6 still lands in.
        PanelColumn.MinWidth = show ? 200 : 0;
        PanelColumn.Width = show ? new GridLength(Math.Clamp(_model.Settings.DescriptionPanelWidth, 200, 800)) : new GridLength(0);
        if (_galleryRow is not null) ShowGalleryDescription(_galleryRow);
        if (announce) Services.Announcer.Announce(show ? "Description panel shown" : "Description panel hidden");
    }

    private void RememberPanelWidth()
    {
        if (PanelColumn.ActualWidth < 200) return;
        _model.Settings.DescriptionPanelWidth = Math.Round(PanelColumn.ActualWidth);
        _model.SaveSettings();
    }

    private void PanelSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => RememberPanelWidth();

    // MARK: Status bar

    /// <summary>The status bar's sections that have something to say, in order.</summary>
    internal List<TextBlock> StatusSections() =>
        new[] { StatusBarText, ActivityText, BackgroundText }.Where(t => t.IsVisible || t == StatusBarText).ToList();

    /// <summary>Ctrl+F9: the status bar's sections, spoken from wherever you are, without moving focus.</summary>
    internal void ReadStatusBar() =>
        Services.Announcer.Announce(string.Join(". ", StatusSections().Select(t => t.Text.Trim()).Where(t => t.Length > 0)) is { Length: > 0 } text
            ? text : "The status bar is empty", interrupt: true);

    /// <summary>F6 (or Ctrl+Shift+F9) to the status bar: its first section, announced as the status bar, as QuickMail does.</summary>
    internal void FocusStatusBar()
    {
        StatusBarText.Focus();
        Services.Announcer.Announce($"Status bar: {StatusBarText.Text}");
    }

    /// <summary>When the section you're on goes away (the export finished), focus stays in the status bar.</summary>
    private void StatusSection_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && sender is TextBlock { IsKeyboardFocused: true }) StatusBarText.Focus();
    }

    /// <summary>Left and Right move between the sections, wrapping; Escape goes back to the list or gallery.</summary>
    internal void StatusBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            FocusContent();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Left or Key.Right)) return;
        var sections = StatusSections();
        var at = sections.FindIndex(t => t.IsKeyboardFocused);
        var next = at < 0 ? 0 : (at + (e.Key == Key.Right ? 1 : -1) + sections.Count) % sections.Count;
        sections[next].Focus();
        e.Handled = true;
    }

    // MARK: View mode and gallery

    private void SetViewMode(ViewMode mode)
    {
        _model.ViewMode = mode;
        ApplyViewMode();
        FocusContent();
        Services.Announcer.Announce(mode == ViewMode.Gallery ? "Gallery view" : "List view");
    }

    private void ApplyViewMode()
    {
        var gallery = _model.ViewMode == ViewMode.Gallery;
        // The list stays in the tree, hidden, so its selection is still the gallery's position.
        FileList.Visibility = gallery ? Visibility.Hidden : Visibility.Visible;
        GalleryPanel.Visibility = gallery ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyText();
        if (gallery)
        {
            _model.EnsureGallerySelection();
            UpdateGallery();
        }
    }

    private async void UpdateGallery()
    {
        var row = _model.FocusedRow;
        var position = _model.GalleryPosition;
        // Not disabled at the ends: a disabled button that has focus drops it to the window. At an end,
        // pressing beeps instead, and the help text says so.
        AutomationProperties.SetHelpText(PreviousButton, position is { Index: 0 } ? "This is the first item" : "");
        AutomationProperties.SetHelpText(NextButton, position is { } p && p.Index + 1 >= p.Count ? "This is the last item" : "");
        GalleryPositionText.Text = position is { } pos ? $"{pos.Index + 1} of {pos.Count}" : "";

        if (row is null)
        {
            _galleryShownId = null;
            GalleryName.Text = GalleryDetails.Text = GalleryCaption.Text = "";
            AutomationProperties.SetName(GalleryImage, _model.IsLoading ? "Loading" : "No items");
            GalleryImage.Show(null, _model.IsLoading ? "Loading…" : "There is nothing here.");
            return;
        }

        GalleryName.Text = row.Name;
        GalleryDetails.Text = string.Join(" · ", new[] { row.Kind, row.DateText, row.Dimensions }.Where(s => s.Length > 0));
        GalleryCaption.Text = row.Caption;
        GalleryCaption.Visibility = row.Caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The description arrives a few seconds later, from automatic descriptions.
        if (_galleryRow != row)
        {
            if (_galleryRow is not null) _galleryRow.PropertyChanged -= GalleryRow_PropertyChanged;
            _galleryRow = row;
            row.PropertyChanged += GalleryRow_PropertyChanged;
        }
        ShowGalleryDescription(row);
        // Just the name: arrowing through the gallery says which photo, then its description follows. The rest is in
        // the footer below (Tab to it).
        AutomationProperties.SetName(GalleryImage, row.Name);
        if (_galleryShownId == row.Id) return;
        _galleryShownId = row.Id;

        if (row.Item is not { } item)
        {
            GalleryImage.Show(null, $"{row.Name}\n{row.Kind}");
            return;
        }
        GalleryImage.Show(null, "Loading image…");
        var image = await GalleryImageFor(item);
        if (_galleryShownId != row.Id) return;
        GalleryImage.Show(image, "This picture can't be shown.");
        CacheNeighbors();
    }

    private void GalleryRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileRow.Description) && sender is FileRow row && row == _galleryRow) ShowGalleryDescription(row);
    }

    private void ShowGalleryDescription(FileRow row)
    {
        GalleryDescription.Text = row.Description;
        // In the Description panel instead, while that's showing.
        GalleryDescription.Visibility = row.Description.Length > 0 && !_model.Settings.ShowDescriptionPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task<ImageSource?> GalleryImageFor(MediaItem item)
    {
        if (!_galleryCache.TryGetValue(item.Path, out var task))
        {
            task = ImageDecoding.LoadForDisplayAsync(item.Path, item.IsVideo, 2400);
            _galleryCache[item.Path] = task;
            _galleryOrder.AddFirst(item.Path);
            while (_galleryOrder.Count > 6)
            {
                _galleryCache.Remove(_galleryOrder.Last!.Value);
                _galleryOrder.RemoveLast();
            }
        }
        return task;
    }

    /// <summary>Starts loading the pictures on either side so moving through the gallery feels instant.</summary>
    private void CacheNeighbors()
    {
        if (_model.GalleryPosition is not { } p) return;
        foreach (var i in new[] { p.Index + 1, p.Index - 1 })
            if (i >= 0 && i < _model.Rows.Count && _model.Rows[i].Item is { } item) _ = GalleryImageFor(item);
    }

    private void Gallery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // Previous and Next keep Space and Enter for pressing themselves; the read-only text boxes keep their keys.
        if (e.OriginalSource is ButtonBase && e.Key is Key.Space or Key.Enter) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: _model.MoveSelection(-1); break;
            case Key.Right or Key.Down: _model.MoveSelection(1); break;
            case Key.Home: if (_model.Rows.Count > 0) _model.MoveSelection(-_model.GalleryPosition?.Index ?? 0); break;
            case Key.End: if (_model.Rows.Count > 0) _model.MoveSelection(_model.Rows.Count - 1 - (_model.GalleryPosition?.Index ?? 0)); break;
            case Key.Space: ShowPreview(); break;
            case Key.Enter: _model.Open(); break;
            case Key.Back: _model.GoToEnclosingFolder(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void GalleryImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        GalleryImage.Focus();
        if (e.ClickCount == 2) _model.Open();
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _model.MoveSelection(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _model.MoveSelection(1);

    // MARK: Dialogs

    private void ShowPreview()
    {
        var rows = _model.SelectedRows.Where(r => r.Item is not null).ToList();
        if (rows.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
        // One item previews the whole list, so arrows keep going; several preview just those.
        var set = rows.Count == 1 ? _model.Rows.Where(r => r.Item is not null).ToList() : rows;
        PreviewWindow? preview = null;
        preview = new PreviewWindow(set, set.IndexOf(rows[0])) { Owner = this, Describe = (r, stillOn, again) => DescribeRowAsync(r, stillOn, again, preview!) };
        preview.ShowDialog();
        if (preview.CurrentRow is { } last && rows.Count == 1) Model_SelectionRequested([last.Id], false);
        FocusContent();
    }

    /// <summary>Keyboard focus is in a box you can type in.</summary>
    internal static bool IsTyping() => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false };

    private void DescribePhoto(bool again)
    {
        if (_model.FocusedRow is not { } row) return;
        _ = DescribeRowAsync(row, () => IsActive && _model.FocusedRow == row, again, this);
    }

    /// <summary>Describe Photo or Describe Again, from here or Preview. Describe Again on a description the person
    /// corrected asks first, as on the Mac.</summary>
    internal Task DescribeRowAsync(FileRow row, Func<bool> stillOn, bool again, Window owner)
    {
        if (AutoDescriber is null)
        {
            Services.Announcer.Announce("Describing photos isn't available.");
            return Task.CompletedTask;
        }
        // Already being described: say so before asking anything (DescribeNowAsync says "Still describing").
        if (row.Item is { } busy && AutoDescriber.IsDescribing(busy.Path)) return AutoDescriber.DescribeNowAsync(row, stillOn, again);
        string? replacing = null;
        var kind = _model.Settings.DescriptionKind;
        if (again && row.Item is { } item && _model.Descriptions?.IsEdited(item, kind) == true)
        {
            if (MessageBox.Show(owner, "Replace the description you edited?", "Describe Again", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return Task.CompletedTask;
            // The correction agreed to; one typed after this is still kept.
            replacing = _model.Descriptions.Get(item, kind);
        }
        return AutoDescriber.DescribeNowAsync(row, stillOn, again, replacing);
    }

    private void ShowInfo()
    {
        DescriptionPanel.SaveIfChanged();
        var kindBefore = _model.Settings.DescriptionKind;
        var row = _model.SelectedRows.FirstOrDefault(r => r.Item is not null);
        if (row?.Item is null) { System.Media.SystemSounds.Beep.Play(); return; }
        new InfoWindow(row, _model.Settings, _model.SaveSettings, describeNow: false, _model.Descriptions,
            useAsCaption: (r, info, kind) => UseDescriptionAsCaptionAsync([r], info, kind)) { Owner = this }.ShowDialog();
        // Get Info can change the Description Type.
        if (_model.Settings.DescriptionKind != kindBefore) _model.DescriptionKindChanged();
        // The rows may have been rebuilt while it was open, so the element WPF would return to can be gone.
        FocusContent();
    }

    /// <param name="chosen">The photos to edit (the Description panel's one); otherwise the selection.</param>
    /// <param name="refocus">Where focus goes afterwards; otherwise the list or gallery.</param>
    private async void EditCaption(IReadOnlyList<FileRow>? chosen = null, Action? refocus = null)
    {
        var rows = (chosen ?? _model.SelectedRows).Where(r => r.Item is not null).ToList();
        if (rows.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
        if (!CanChangePhotos()) return;
        if (!ConfirmBulkWrite("Edit Caption", rows.Select(r => r.Item!).ToList())) return;
        // A caption not read yet would show as blank, and saving would erase it. Read it first.
        var initial = "";
        if (rows.Count == 1)
        {
            var item = rows[0].Item!;
            if (item.Metadata is null)
            {
                try { initial = (await MetadataReader.ReadAsync(item.Path)).Caption; }
                catch (Exception ex)
                {
                    ShowError($"The caption of {item.Name} couldn't be read, so it can't be edited safely: {ex.Message}");
                    return;
                }
            }
            else initial = item.Metadata.Caption;
        }
        var dialog = new CaptionDialog(rows.Count, initial) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (refocus is not null) refocus();
        else FocusContent();
        if (!saved) return;
        var text = dialog.Caption.Trim();
        if (text == initial.Trim() && rows.Count == 1) return;
        // Several items start with an empty box, so an empty Save would wipe every caption. Ask first.
        if (text.Length == 0 && rows.Count > 1 && MessageBox.Show(this,
                $"Remove the captions from all {rows.Count} items?", "Remove Captions",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _model.Fire(_model.SaveCaptionAsync(rows, text));
    }

    /// <summary>Changing many files, or files that are only in the cloud (each is downloaded to change it),
    /// is checked with the person first.</summary>
    private bool ConfirmBulkWrite(string action, IReadOnlyList<MediaItem>? chosen = null, Window? owner = null)
    {
        var files = chosen ?? _model.SelectedRows.Select(r => r.Item).OfType<MediaItem>().ToList();
        var online = files.Count(f => f.IsOnlineOnly);
        if (files.Count <= 50 && online == 0) return true;
        var message = $"{action} changes {files.Count:N0} files on your PC.";
        if (online > 0) message += $" {online:N0} of them are online-only, and each will be downloaded to change it.";
        return MessageBox.Show(owner ?? this, message + " Continue?", action, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private void ExportSelection()
    {
        var rows = _model.SelectedRows.Where(r => r.Item is not null).ToList();
        if (rows.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
        var dialog = new OpenFolderDialog
        {
            Title = rows.Count == 1 ? "Choose a folder to export 1 item to" : $"Choose a folder to export {rows.Count} items to",
        };
        if (dialog.ShowDialog(this) == true) _model.Fire(_model.ExportAsync(rows, dialog.FolderName));
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = $"Choose a folder to add to {AppInfo.Name}'s library" };
        if (dialog.ShowDialog(this) == true) _model.Fire(_model.AddFolderAsync(dialog.FolderName));
    }

    private void RemoveFolder()
    {
        if (_model.CurrentRoot is not { } root) return;
        var answer = MessageBox.Show(this,
            $"Remove “{Path.GetFileName(root)}” from {AppInfo.Name}? The folder and its photos stay on your PC; {AppInfo.Name} just stops listing them.\n\n{root}",
            "Remove Folder from Library", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _model.Fire(_model.RemoveFolderAsync(root));
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"{AppInfo.Name} {Services.Updates.AppVersion}\n\nBrowse your Pictures library like folders in File Explorer, built to work well with screen readers.\n\nFolders in the library:\n{string.Join("\n", _model.Roots)}",
            $"About {AppInfo.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool _updating;

    private async void CheckForUpdates()
    {
        if (_updating)
        {
            Services.Announcer.Announce("Still checking for updates");
            return;
        }
        _updating = true;
        try
        {
            Services.Announcer.Announce("Checking for updates");
            var result = await Services.Updates.CheckAsync();
            switch (result.State)
            {
                case Services.Updates.State.NoIdentity:
                    Inform($"This copy of {AppInfo.Name} isn't installed, so it can't update itself.");
                    break;
                case Services.Updates.State.NotFromAppInstaller:
                    Inform($"{AppInfo.Name} was installed without PhotoFinder.appinstaller, so Windows doesn't know where its updates are. " +
                           "Open PhotoFinder.appinstaller to install it again; your settings are kept.");
                    break;
                case Services.Updates.State.UpToDate:
                    Inform($"{AppInfo.Name} is up to date (version {Services.Updates.PackageVersion}).");
                    break;
                case Services.Updates.State.Failed:
                    ShowError($"{AppInfo.Name} couldn't check for updates. {result.Message}");
                    break;
                case Services.Updates.State.Available:
                    await InstallUpdateAsync(result.Source!);
                    break;
            }
        }
        finally
        {
            _updating = false;
        }

        void Inform(string message) => MessageBox.Show(this, message, "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task InstallUpdateAsync(Uri source)
    {
        if (MessageBox.Show(this, $"A new version of {AppInfo.Name} is available. Install it now? {AppInfo.Name} closes, updates, and opens again.",
                "Check for Updates", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        // Windows closes Photo Finder itself to install, without the usual closing steps, so nothing may be half written.
        if (_model.IsWriting)
        {
            MessageBox.Show(this, $"{AppInfo.Name} is still saving changes to your photos. Check for updates again when it's finished.",
                "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _model.SaveCaches();
        await _model.StopCaptionsAsync();
        Services.Announcer.Announce($"Downloading the update. {AppInfo.Name} will close and open again when it's ready.");
        if (await Services.Updates.InstallAsync(source) is { } error) ShowError($"The update couldn't be installed. {error}");
    }

    // MARK: Check System and Report a Bug

    /// <summary>The facts for Help > Check System and bug reports. Asked at once rather than one after another;
    /// Windows AI isn't asked at all on a Windows too old for it, since starting the helper can take a while.</summary>
    internal async Task<Services.SystemCheck> RunSystemCheckAsync(CancellationToken cancel = default)
    {
        var os = Environment.OSVersion.Version;
        var folders = _model.Roots.ToList();
        var readiness = os.Build >= Services.SystemCheck.MinimumBuild
            ? Services.WindowsAI.CheckAsync(cancel) : Task.FromResult<Services.AIReadiness>(null!);
        var npu = Services.SystemCheck.FindNpuAsync();
        // A disconnected network folder can take many seconds to answer.
        var missing = Task.Run(() => folders.Where(f => !Directory.Exists(f)).ToList(), cancel);
        await Task.WhenAll(readiness, npu, missing);
        cancel.ThrowIfCancellationRequested();
        return new Services.SystemCheck(new Services.SystemCheck.Facts
        {
            AppVersion = Services.Updates.AppVersion,
            UpdatesItself = Services.Updates.UpdatesItself,
            OsVersion = os,
            Npu = npu.Result,
            Readiness = readiness.Result,
            LibraryFolders = folders,
            MissingFolders = missing.Result,
            SavedDescriptions = _model.Descriptions?.PhotoCount ?? 0,
            DescribeAutomatically = AutoDescriber?.Enabled ?? false,
            DescribeInBackground = BackgroundDescriber?.Enabled ?? false,
        });
    }

    private void ShowCheckSystem()
    {
        new CheckSystemWindow(RunSystemCheckAsync, ReportBug) { Owner = this }.ShowDialog();
        FocusContent();
    }

    private bool _reporting;

    /// <summary>Help > Report a Bug…: a new GitHub issue in the browser, prefilled with the questions and the system
    /// check, and the same text on the clipboard, as on the Mac. Until the app kit's relay is set up (#33). The report
    /// leaves out folder paths, since an issue is public.</summary>
    private async void ReportBug(Services.SystemCheck? check)
    {
        if (_reporting)
        {
            Services.Announcer.Announce("Still preparing the bug report");
            return;
        }
        _reporting = true;
        try
        {
            if (check is null)
            {
                Services.Announcer.Announce("Preparing a bug report");
                check = await RunSystemCheckAsync();
            }
            var body = Services.BugReport.Body(check.ReportText);
            var copied = false;
            try
            {
                Clipboard.SetText(body);
                copied = true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another app holding the clipboard.
            }
            var uri = Services.BugReport.NewIssueUri(body, out var includesBody);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                Services.Announcer.Announce(
                    includesBody && copied ? "Opening a new bug report in your browser. The details are also on the clipboard."
                    : includesBody ? "Opening a new bug report in your browser."
                    : copied ? "Opening a new bug report in your browser. Paste the details from the clipboard into it."
                    : "Opening a new bug report in your browser.");
                if (!includesBody && !copied) ShowReport(body);
            }
            catch (Exception)
            {
                if (copied)
                    ShowError($"The browser couldn't be opened. The bug report is on the clipboard: paste it into a new issue at {AppInfo.IssuesUrl}.");
                else
                    ShowReport(body);
            }
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>When neither the browser nor the clipboard could take the report: shown, so it can still be copied.</summary>
    private void ShowReport(string body) =>
        MessageBox.Show(this,
            $"Press Ctrl+C to copy this report, then paste it into a new issue at {AppInfo.IssuesUrl}.\n\n{body}",
            "Report a Bug", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Over the window in front (Get Info, say), so focus goes back there afterwards, not to this one behind it.</summary>
    private void ShowError(string message, Window? owner = null) =>
        MessageBox.Show(owner ?? ActiveWindow, message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);

    private Window ActiveWindow => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;

    private bool _closingAfterCaptions;
}
