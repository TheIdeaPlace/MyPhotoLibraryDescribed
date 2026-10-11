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
    };

    public MainWindow(LibraryModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        BindCommands();
        BuildColumns();
        BuildSortMenu();

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
                ShowError($"Photo Finder couldn't read your library: {ex.Message}");
            }
            UpdateEmptyText();
            FocusContent();
        };
        model.FilesChanged += paths => { foreach (var p in paths) _thumbnails.Forget(p); };
        Closing += (_, e) =>
        {
            if (model.IsWriting && MessageBox.Show(this,
                    "Photo Finder is still saving changes to your photos. If you quit now, the rest won't be changed. Quit anyway?",
                    "Still Saving", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            AutoDescriber?.Dispose();
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
        Bind(AppCommands.GetInfo, () => ShowInfo(describe: false), HasFiles);
        Bind(AppCommands.DescribeImage, () => ShowInfo(describe: true), () => _model.SelectedRows.Any(r => r.Item is { IsVideo: false }));
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
        Bind(AppCommands.ToggleFavorite, () => { if (_model.IsWriting || ConfirmBulkWrite("Toggle Favorite")) _model.Fire(_model.ToggleFavoriteAsync()); }, HasFiles);
        Bind(AppCommands.EditCaption, EditCaption, HasFiles);
        Bind(AppCommands.About, ShowAbout);
        Bind(AppCommands.ChooseColumns, ChooseColumns);
        Bind(AppCommands.AutoDescribe, () =>
        {
            if (AutoDescriber is null) return;
            AutoDescriber.Enabled = !AutoDescriber.Enabled;
            AutoDescribeItem.IsChecked = AutoDescriber.Enabled;
        }, () => AutoDescriber is not null);
        Loaded += (_, _) =>
        {
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
                if (int.TryParse(e.Parameter?.ToString(), out var stars) && (_model.IsWriting || ConfirmBulkWrite("Set Rating"))) _model.Fire(_model.SetRatingAsync(stars));
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
            var item = new MenuItem { Header = column.Title, IsCheckable = true, IsChecked = _model.SortColumn == column.Id, Tag = column.Id };
            item.Click += (_, _) => _model.SortBy(column.Id);
            SortMenu.Items.Add(item);
        }
    }

    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
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
            case nameof(LibraryModel.Title):
                UpdateTitle();
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

    private void UpdateTitle() => Title = _model.CurrentSource is null ? "Photo Finder" : $"{_model.Title} - Photo Finder";

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

    /// <summary>F6 and Shift+F6 move between the sidebar, the list or gallery, and search.</summary>
    private void MovePane(int direction)
    {
        var panes = new List<Action> { FocusSidebarSelection, FocusContent, () => SearchBox.Focus() };
        var current = Sidebar.IsKeyboardFocusWithin ? 0 : SearchBox.IsKeyboardFocusWithin ? 2 : 1;
        panes[(current + direction + panes.Count) % panes.Count]();
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
        AutomationProperties.SetName(GalleryImage, row.SpokenDescription);
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
        GalleryDescription.Visibility = row.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var preview = new PreviewWindow(set, set.IndexOf(rows[0])) { Owner = this };
        preview.ShowDialog();
        if (preview.CurrentRow is { } last && rows.Count == 1) Model_SelectionRequested([last.Id], false);
        FocusContent();
    }

    private void ShowInfo(bool describe)
    {
        var row = _model.SelectedRows.FirstOrDefault(r => r.Item is not null && (!describe || !r.Item.IsVideo));
        if (row?.Item is null) { System.Media.SystemSounds.Beep.Play(); return; }
        new InfoWindow(row, _model.Settings, _model.SaveSettings, describe, _model.Descriptions) { Owner = this }.ShowDialog();
        // The rows may have been rebuilt while it was open, so the element WPF would return to can be gone.
        FocusContent();
    }

    private async void EditCaption()
    {
        var rows = _model.SelectedRows.Where(r => r.Item is not null).ToList();
        if (rows.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
        if (!ConfirmBulkWrite("Edit Caption")) return;
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
        FocusContent();
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
    private bool ConfirmBulkWrite(string action)
    {
        var files = _model.SelectedRows.Select(r => r.Item).OfType<MediaItem>().ToList();
        var online = files.Count(f => f.IsOnlineOnly);
        if (files.Count <= 50 && online == 0) return true;
        var message = $"{action} changes {files.Count:N0} files on your PC.";
        if (online > 0) message += $" {online:N0} of them are online-only, and each will be downloaded to change it.";
        return MessageBox.Show(this, message + " Continue?", action, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
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
        var dialog = new OpenFolderDialog { Title = "Choose a folder to add to Photo Finder's library" };
        if (dialog.ShowDialog(this) == true) _model.Fire(_model.AddFolderAsync(dialog.FolderName));
    }

    private void RemoveFolder()
    {
        if (_model.CurrentRoot is not { } root) return;
        var answer = MessageBox.Show(this,
            $"Remove “{Path.GetFileName(root)}” from Photo Finder? The folder and its photos stay on your PC; Photo Finder just stops listing them.\n\n{root}",
            "Remove Folder from Library", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _model.Fire(_model.RemoveFolderAsync(root));
    }

    private void ShowAbout()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this,
            $"Photo Finder {version}\n\nBrowse your Pictures library like folders in File Explorer, built to work well with screen readers.\n\nFolders in the library:\n{string.Join("\n", _model.Roots)}",
            "About Photo Finder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
}
