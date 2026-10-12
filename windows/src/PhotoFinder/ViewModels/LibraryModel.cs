using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// The library and everything the window shows of it. Runs on the UI thread; scanning and reading
/// properties happen in the background and come back here to update rows in place.
/// </summary>
public sealed class LibraryModel : INotifyPropertyChanged, IDisposable
{
    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly MetadataCache _cache;
    private readonly Func<Task<List<string>>> _picturesLibrary;
    private readonly Func<DateTime> _clock;

    private List<MediaItem> _items = [];
    private List<string> _picturesFolders = [];
    private List<FileRow> _allRows = [];
    private Dictionary<string, FileRow> _rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files whose details have been tried, by path, size and date: a file that changes is read again.</summary>
    private readonly HashSet<string> _detailsTried = new(StringComparer.OrdinalIgnoreCase);
    private SidebarItem? _currentSource;
    private string _searchText = "";
    private bool _isLoading = true;
    private string? _activity;
    private string? _detailProgress;
    private IReadOnlyList<FileRow> _selectedRows = [];
    private CancellationTokenSource? _fillCancel;
    private Task _fillTask = Task.CompletedTask;
    private int _scanGeneration;
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _rescanDebounce;
    private DateTime _ignoreChangesUntil;

    public LibraryModel(Settings settings, string settingsPath, MetadataCache cache,
        Func<Task<List<string>>> picturesLibrary, Func<DateTime>? clock = null)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _cache = cache;
        _picturesLibrary = picturesLibrary;
        _clock = clock ?? (() => DateTime.Now);
        FileRow.SpokenColumns = _settings.ShownColumnIds();
    }

    // MARK: State

    public ObservableCollection<SidebarItem> Sections { get; } = [];
    public Dictionary<string, SidebarItem> ItemsById { get; private set; } = [];
    /// <summary>The rows the list shows, filtered and sorted. Replaced whole rather than edited, so a big
    /// list appears in one step.</summary>
    public List<FileRow> Rows { get; private set; } = [];
    public IReadOnlyList<string> Roots { get; private set; } = [];
    public IReadOnlyList<MediaItem> AllItems => _items;
    public Settings Settings => _settings;

    /// <summary>Saved Windows AI descriptions, shared by automatic descriptions and Get Info.</summary>
    public DescriptionStore? Descriptions { get; init; }

    public SidebarItem? CurrentSource
    {
        get => _currentSource;
        private set { _currentSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); }
    }

    public string Title => CurrentSource?.Title ?? AppInfo.Name;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            var keep = SelectedIds();
            _searchText = value;
            OnPropertyChanged();
            ApplyView(keep, focus: false);
            // The box updates after a pause in typing, so this speaks once per pause, not per letter.
            if (value.Trim().Length > 0)
                Announcer.Announce(Rows.Count == 0 ? "No results" : Rows.Count == 1 ? "1 result" : $"{Rows.Count:N0} results");
        }
    }

    public string SortColumn => _settings.SortColumn;
    public bool SortDescending => _settings.SortDescending;

    public ViewMode ViewMode
    {
        get => _settings.ViewMode;
        set
        {
            if (_settings.ViewMode == value) return;
            _settings.ViewMode = value;
            SaveSettings();
            OnPropertyChanged();
            if (value == ViewMode.Gallery) EnsureGallerySelection();
        }
    }

    /// <summary>Set by the view whenever the list's selection changes.</summary>
    public IReadOnlyList<FileRow> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value;
            UpdateFocusedRow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FocusedRow));
            OnPropertyChanged(nameof(GalleryPosition));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            _isLoading = value;
            OnPropertyChanged();
            OnStatusChanged();
            // Refresh is disabled while loading; WPF re-checks commands only on input unless asked.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>What's being done right now ("Exporting 2 of 5…"), shown in the status bar.</summary>
    public string? Activity
    {
        get => _activity;
        private set { _activity = value; OnPropertyChanged(); OnStatusChanged(); }
    }

    public string? DetailProgress
    {
        get => _detailProgress;
        private set { _detailProgress = value; OnPropertyChanged(); OnStatusChanged(); }
    }

    public bool IsBusy => IsLoading || Activity is not null || DetailProgress is not null || BackgroundProgress is not null;

    public string StatusText
    {
        get
        {
            if (IsLoading) return "Loading…";
            var count = Rows.Count;
            var items = count == 1 ? "1 item" : $"{count:N0} items";
            return SelectedRows.Count == 0 ? items : $"{SelectedRows.Count:N0} of {items} selected";
        }
    }

    /// <summary>What's being done right now (an export, reading details), its own status bar section.</summary>
    public string? CurrentActivity => Activity ?? DetailProgress;

    /// <summary>The view should select these rows (by id), and move keyboard focus to the first if asked.</summary>
    public event Action<IReadOnlyList<string>, bool>? SelectionRequested;
    public event Action<string>? ErrorRaised;
    /// <summary>The columns shown in the list changed.</summary>
    public event Action? ColumnsChanged;
    /// <summary>Raised around replacing the sidebar's items, so the view can keep focus in the sidebar.</summary>
    public event Action? SidebarRebuilding;
    public event Action? SidebarRebuilt;

    // MARK: Startup and scanning

    public async Task StartAsync()
    {
        _picturesFolders = await _picturesLibrary();
        await RescanAsync(firstTime: true);
    }

    /// <summary>Lists the library's files again, keeping every detail already read.</summary>
    public async Task RescanAsync(bool firstTime = false)
    {
        var generation = ++_scanGeneration;
        Roots = LibraryRoots.Combine(_picturesFolders, _settings);
        OnPropertyChanged(nameof(Roots));
        if (firstTime) IsLoading = true;
        var roots = Roots.ToList();
        var cache = _cache;
        var scanned = await Task.Run(() =>
        {
            var found = LibraryScanner.Scan(roots);
            foreach (var item in found) item.Metadata = cache.Lookup(item);
            return found;
        });
        if (generation != _scanGeneration) return;

        // Keep details read this session that the cache hasn't saved yet.
        var previous = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var item in scanned)
        {
            if (item.Metadata is null && previous.TryGetValue(item.Path, out var old)
                && old.Size == item.Size && old.Modified == item.Modified)
                item.Metadata = old.Metadata;
        }
        // Nothing changed on disk (OneDrive touching a folder, say): leave the list, its focus and its scroll alone.
        var unchanged = CurrentSource is not null && scanned.Count == _items.Count
            && scanned.All(i => previous.TryGetValue(i.Path, out var old) && old.Size == i.Size && old.Modified == i.Modified
                && old.IsOnlineOnly == i.IsOnlineOnly);
        if (unchanged) return;
        _items = scanned;
        IsLoading = false;
        var before = CurrentSource;
        RebuildSidebar();
        // The first scan, or one that replaced it (F5 while loading), opens All Photos. If the rebuild already
        // moved somewhere because the place was gone, those rows are already fresh.
        if (CurrentSource is null) Navigate(SidebarIds.AllPhotos, announce: false);
        else if (CurrentSource.Id == before?.Id) ReloadCurrent(keepSelection: true);
        await WatchRootsAsync();
        StartDetailFill();
    }

    private void RebuildSidebar()
    {
        var built = SidebarBuilder.Build(_items, Roots, _clock());
        if (built.Count == Sections.Count && built.Zip(Sections).All(p => p.First.SameAs(p.Second))) return;

        // Keep what was open and what was selected.
        var firstBuild = ItemsById.Count == 0;
        var expanded = ItemsById.Values.Where(i => i.IsExpanded).Select(i => i.Id).ToHashSet();
        var selectedId = CurrentSource?.Id;
        ItemsById = SidebarBuilder.Index(built);
        foreach (var item in ItemsById.Values)
        {
            item.IsExpanded = expanded.Contains(item.Id) || (firstBuild && item.Kind == SourceKind.Section);
        }
        SidebarRebuilding?.Invoke();
        Sections.Clear();
        foreach (var section in built) Sections.Add(section);
        SidebarRebuilt?.Invoke();

        if (selectedId is not null)
        {
            if (ItemsById.TryGetValue(selectedId, out var same))
            {
                _currentSource = same;
                same.IsSelected = true;
                OnPropertyChanged(nameof(CurrentSource));
                OnPropertyChanged(nameof(Title));
                // A container's rows are its children, which may have changed.
                if (same.HasChildren) ReloadCurrent(keepSelection: true);
            }
            else
            {
                // The place is gone (its last photo left it): say where the list went instead.
                var gone = CurrentSource?.Title;
                Navigate(SidebarIds.AllPhotos, announce: false);
                Announcer.Announce($"{gone} is no longer in the library. Showing All Photos, {CountText(Rows.Count)}");
            }
        }
    }

    // MARK: Details

    private void StartDetailFill()
    {
        _fillCancel?.Cancel();
        var cancel = _fillCancel = new CancellationTokenSource();
        _fillTask = FillDetailsAsync(cancel.Token);
    }

    /// <summary>For tests: finishes when the properties have all been read.</summary>
    internal Task DetailFillTask => _fillTask;

    /// <summary>Reads properties a batch at a time, the list being shown first in the order it's shown, so the
    /// rows people reach first are done first. Online-only cloud files are skipped: reading them would download them.</summary>
    private async Task FillDetailsAsync(CancellationToken cancel)
    {
        var total = _items.Count(NeedsDetails);
        if (total == 0) { DetailProgress = null; return; }
        var done = 0;
        var started = Stopwatch.StartNew();
        var anyRead = false;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var batch = Rows.Select(r => r.Item).OfType<MediaItem>().Where(NeedsDetails).Take(100).ToList();
                if (batch.Count < 100) batch.AddRange(_items.Where(i => NeedsDetails(i) && !batch.Contains(i)).Take(100 - batch.Count));
                if (batch.Count == 0) break;

                var results = await Task.Run(async () =>
                {
                    var read = new (MediaItem Item, MediaMetadata? Metadata)[batch.Count];
                    await Parallel.ForEachAsync(Enumerable.Range(0, batch.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel },
                        async (i, _) =>
                        {
                            MediaMetadata? metadata = null;
                            try { metadata = await MetadataReader.ReadAsync(batch[i].Path); }
                            catch (Exception) { /* unreadable: it keeps its file dates */ }
                            read[i] = (batch[i], metadata);
                        });
                    return read;
                }, cancel);

                foreach (var (item, metadata) in results)
                {
                    // Marked only once read: a pass cancelled mid-batch leaves its files for the next pass.
                    _detailsTried.Add(TriedKey(item));
                    if (metadata is null) continue;
                    item.Metadata = metadata;
                    _cache.Remember(item, metadata);
                    if (_rowsByPath.TryGetValue(item.Path, out var row)) row.Refresh();
                    anyRead = true;
                }
                done += batch.Count;
                DetailProgress = $"Reading photo details, {Math.Min(done, total):N0} of {total:N0}…";
                if (done % 2000 < 100) SaveCacheInBackground();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!cancel.IsCancellationRequested) DetailProgress = null;
        }
        if (cancel.IsCancellationRequested) return;
        Debug.WriteLine($"Read details for {done} items in {started.Elapsed.TotalSeconds:0.0}s");
        _cache.KeepOnly(_items);
        SaveCacheInBackground();
        if (anyRead)
        {
            // Dates, favorites and the other smart albums depend on the details.
            RebuildSidebar();
            if (CurrentSource is { HasChildren: false }) ReloadCurrent(keepSelection: true);
            else ApplyView(SelectedIds(), focus: false);
        }
    }

    private bool NeedsDetails(MediaItem item) => item.Metadata is null && !item.IsOnlineOnly && !_detailsTried.Contains(TriedKey(item));

    private static string TriedKey(MediaItem item) => $"{item.Path}|{item.Size}|{item.Modified.Ticks}";

    // MARK: Navigation

    public void Navigate(string id, string? selectChildId = null, bool announce = true)
    {
        if (!ItemsById.TryGetValue(id, out var item)) return;
        for (var parent = item.ParentId; parent is not null && ItemsById.TryGetValue(parent, out var p); parent = p.ParentId)
            p.IsExpanded = true;
        var old = CurrentSource;
        // Set first: selecting the sidebar item calls back into SidebarSelected, which then sees it's already here.
        CurrentSource = item;
        if (old is not null && old != item) old.IsSelected = false;
        item.IsSelected = true;
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        LoadRows(selectChildId is null ? [] : [selectChildId], focus: selectChildId is not null);
        if (announce) Announcer.Announce($"{item.Title}, {CountText(Rows.Count)}");
    }

    /// <summary>The sidebar's selection moved: show that place, without moving focus away from the sidebar.</summary>
    public void SidebarSelected(SidebarItem item)
    {
        if (item == CurrentSource) return;
        Navigate(item.Id, announce: false);
    }

    public void GoToEnclosingFolder()
    {
        if (CurrentSource is not { ParentId: { } parentId } current)
        {
            Beep();
            return;
        }
        Navigate(parentId, current.Id);
    }

    private void LoadRows(IReadOnlyList<string> select, bool focus)
    {
        if (CurrentSource is null) return;
        _allRows = RowQuery.RowsFor(CurrentSource, _items, _clock());
        _rowsByPath = _allRows.Where(r => r.Item is not null).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        ApplyView(select, focus);
        if (ViewMode == ViewMode.Gallery) EnsureGallerySelection();
        // Read the new list's details first.
        if (_items.Any(NeedsDetails) && _fillTask.IsCompleted) StartDetailFill();
    }

    private void ReloadCurrent(bool keepSelection)
    {
        var keep = keepSelection ? SelectedIds() : [];
        _fallbackIndex = keepSelection ? FocusedIndex : -1;
        try { LoadRows(keep, focus: false); }
        finally { _fallbackIndex = -1; }
    }

    /// <summary>While reloading: where the selection was, so if its row has gone (unfavorited inside
    /// Favorites, deleted on disk) the row that took its place is selected instead of jumping to the top.</summary>
    private int _fallbackIndex = -1;

    /// <summary>Filters and sorts the rows into the list, then asks the view to select <paramref name="select"/>.</summary>
    private void ApplyView(IReadOnlyList<string> select, bool focus)
    {
        var comparer = RowQuery.Comparer(_settings.SortColumn, _settings.SortDescending);
        var visible = _allRows.Where(r => RowQuery.MatchesSearch(r, _searchText)).Order(comparer).ToList();
        Rows = visible;
        UpdateFocusedRow();
        OnPropertyChanged(nameof(Rows));
        var ids = new HashSet<string>(Rows.Select(r => r.Id));
        var keep = select.Where(ids.Contains).ToList();
        if (keep.Count == 0 && select.Count > 0 && _fallbackIndex >= 0 && Rows.Count > 0)
            keep = [Rows[Math.Min(_fallbackIndex, Rows.Count - 1)].Id];
        SelectionRequested?.Invoke(keep, focus);
        OnStatusChanged();
    }

    private List<string> SelectedIds() => SelectedRows.Select(r => r.Id).ToList();

    public void SortBy(string column)
    {
        if (_settings.SortColumn == column) _settings.SortDescending = !_settings.SortDescending;
        else
        {
            _settings.SortColumn = column;
            // Dates and sizes read best newest or largest first; text reads best A to Z.
            _settings.SortDescending = column is "date" or "modified" or "size" or "dimensions" or "megapixels" or "rating" or "favorite" or "duration";
        }
        SaveSettings();
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        ApplyView(SelectedIds(), focus: false);
        var title = Columns.Find(column)?.Title ?? column;
        Announcer.Announce($"Sorted by {title}, {(_settings.SortDescending ? "descending" : "ascending")}");
    }

    // MARK: Columns

    /// <summary>The columns shown, in the order they're shown and read. Always includes Name.</summary>
    public IReadOnlyList<ColumnInfo> ShownColumns => _settings.ShownColumnIds().Select(id => Columns.Find(id)!).ToList();

    public bool IsColumnVisible(ColumnInfo column) => _settings.IsColumnVisible(column);

    /// <summary>Turns a column on (added at the end) or off. Name can't be turned off.</summary>
    public void SetColumnVisible(ColumnInfo column, bool visible)
    {
        if (column.Id == Columns.Name) return;
        var order = _settings.ShownColumnIds();
        order.Remove(column.Id);
        if (visible) order.Add(column.Id);
        SetColumnOrder(order);
    }

    /// <summary>Shows these columns, in this order, in the list and in what screen readers hear for each row.</summary>
    public void SetColumnOrder(IEnumerable<string> ids)
    {
        _settings.ColumnOrder = Columns.Clean(ids);
        _settings.Columns.Clear();   // the old on/off choices, now carried by ColumnOrder
        SaveSettings();
        FileRow.SpokenColumns = _settings.ColumnOrder;
        foreach (var row in Rows) row.NotifyAccessibleName();
        ColumnsChanged?.Invoke();
    }

    // MARK: Actions

    private List<FileRow> Chosen(IReadOnlyList<FileRow>? rows) => (rows ?? SelectedRows).ToList();

    private static List<MediaItem> Files(IEnumerable<FileRow> rows) => rows.Select(r => r.Item).OfType<MediaItem>().ToList();

    public void Open(IReadOnlyList<FileRow>? rows = null)
    {
        var chosen = Chosen(rows);
        if (chosen.Count > 0 && chosen.All(r => r.IsContainer))
        {
            Navigate(chosen[0].Id);
            return;
        }
        var files = Files(chosen);
        if (files.Count == 0) { Beep(); return; }
        foreach (var file in files)
        {
            try { FileActions.Open(file.Path); }
            catch (Exception ex) { RaiseError($"{file.Name} couldn't be opened: {ex.Message}"); return; }
        }
    }

    public void ShowInExplorer(IReadOnlyList<FileRow>? rows = null)
    {
        var chosen = Chosen(rows).FirstOrDefault();
        var path = chosen?.Item?.Path ?? chosen?.Container?.FolderPath;
        if (path is null) { Beep(); return; }
        FileActions.ShowInExplorer(path);
    }

    /// <summary>Puts the files on the clipboard, to paste into File Explorer, Mail and so on.</summary>
    public void Copy(IReadOnlyList<FileRow>? rows = null)
    {
        var files = Files(Chosen(rows));
        if (files.Count == 0) { Beep(); return; }
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(files.Select(f => f.Path).ToArray());
        try
        {
            System.Windows.Clipboard.SetFileDropList(list);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            RaiseError($"The clipboard is in use by another app. Try again. ({ex.Message})");
            return;
        }
        Announcer.Announce(files.Count == 1 ? $"Copied {files[0].Name}" : $"Copied {files.Count} items");
    }

    public async Task ExportAsync(IReadOnlyList<FileRow> rows, string folder)
    {
        var files = Files(rows);
        if (files.Count == 0) { Beep(); return; }
        var done = 0;
        try
        {
            foreach (var file in files)
            {
                Activity = files.Count == 1 ? "Exporting…" : $"Exporting {done + 1} of {files.Count}…";
                await Task.Run(() => FileActions.ExportInto(file.Path, folder));
                done++;
            }
            Announcer.Announce($"Exported {CountText(done)} to {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RaiseError(done == 0 ? $"Couldn't export: {ex.Message}" : $"Exported {CountText(done)}, then stopped: {ex.Message}");
        }
        finally
        {
            Activity = null;
        }
    }

    public async Task ToggleFavoriteAsync(IReadOnlyList<FileRow>? rows = null)
    {
        var files = Files(Chosen(rows));
        if (files.Count == 0) { Beep(); return; }
        var makeFavorite = !files.All(f => f.IsFavorite);
        var result = await WriteAsync("Updating favorites…", files,
            items => MetadataWriter.SetRatingAsync(items, makeFavorite ? MediaTypes.FavoriteRating : 0));
        if (result.Succeeded > 0) Announcer.Announce(makeFavorite ? "Added to Favorites" : "Removed from Favorites");
    }

    public async Task SetRatingAsync(int stars, IReadOnlyList<FileRow>? rows = null)
    {
        var files = Files(Chosen(rows));
        if (files.Count == 0) { Beep(); return; }
        var result = await WriteAsync("Saving rating…", files, items => MetadataWriter.SetRatingAsync(items, stars));
        if (result.Succeeded > 0) Announcer.Announce(stars == 0 ? "Rating removed" : $"Rated {FileRow.StarsText(stars)}");
    }

    public async Task SaveCaptionAsync(IReadOnlyList<FileRow> rows, string caption)
    {
        var files = Files(rows);
        if (files.Count == 0) return;
        var result = await WriteAsync("Saving caption…", files, items => MetadataWriter.SetCaptionAsync(items, caption));
        if (result.Succeeded > 0) Announcer.Announce(caption.Trim().Length == 0 ? "Caption removed" : "Caption saved");
    }

    /// <summary>Writes to the files, then reads them back so the list shows what's really stored.</summary>
    /// <summary>True while a caption, rating or favorite is being written.</summary>
    public bool IsWriting { get; private set; }

    private async Task<WriteResult> WriteAsync(string label, List<MediaItem> files, Func<IReadOnlyList<MediaItem>, Task<WriteResult>> write)
    {
        // One write at a time: two at once would fight over the same files.
        if (IsWriting)
        {
            Beep();
            Announcer.Announce("Still saving the last change");
            return new WriteResult(0, []);
        }
        IsWriting = true;
        Activity = files.Count > 1 ? $"{label.TrimEnd('…')} for {files.Count:N0} items…" : label;
        // Writing changes the files, which the watcher would otherwise take for outside edits.
        _ignoreChangesUntil = DateTime.MaxValue;
        try
        {
            var result = await Task.Run(() => write(files));
            await Task.Run(async () =>
            {
                foreach (var item in files)
                {
                    var (oldSize, oldModified) = (item.Size, item.Modified);
                    item.RefreshFileInfo();
                    Descriptions?.Rekey(item.Path, oldSize, oldModified, item);
                    try { item.Metadata = await MetadataReader.ReadAsync(item.Path); }
                    catch (Exception) { /* keeps what it had */ }
                    if (item.Metadata is { } m) _cache.Remember(item, m);
                }
            });
            // A rescan may have swapped in new items for the same files meanwhile; give them what was read.
            var current = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                if (current.TryGetValue(item.Path, out var now) && now != item)
                {
                    now.RefreshFileInfo();
                    now.Metadata = item.Metadata;
                }
                if (_rowsByPath.TryGetValue(item.Path, out var row)) row.Refresh();
            }
            FilesChanged?.Invoke(files.Select(f => f.Path).ToList());
            SaveCacheInBackground();
            RebuildSidebar();
            // A photo that stops being a favorite leaves Favorites.
            if (CurrentSource is { Kind: SourceKind.Favorites }) ReloadCurrent(keepSelection: true);
            if (result.Failures.Count > 0) RaiseError(result.FailureSummary);
            return result;
        }
        finally
        {
            _ignoreChangesUntil = DateTime.UtcNow.AddSeconds(3);
            IsWriting = false;
            Activity = null;
        }
    }

    /// <summary>Files Photo Finder changed, so the view can drop their old thumbnails.</summary>
    public event Action<IReadOnlyList<string>>? FilesChanged;

    // MARK: Folders

    public async Task AddFolderAsync(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        _settings.RemovedFolders.RemoveAll(f => f.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (!Roots.Any(r => r.Equals(full, StringComparison.OrdinalIgnoreCase) || LibraryScanner.IsInside(full, r)))
            _settings.ExtraFolders.Add(full);
        SaveSettings();
        await RescanAsync();
        Announcer.Announce($"Added {Path.GetFileName(full)}");
        // The folder's own files, listed directly: the library's list may be mid-rescan (F5, the file watcher).
        var paths = await Task.Run(() => LibraryScanner.Scan([full]).Select(i => i.Path).ToList());
        FolderAdded?.Invoke(full, paths);
    }

    /// <summary>A folder was added to the library, with the paths of the photos and videos in it.</summary>
    public event Action<string, IReadOnlyList<string>>? FolderAdded;

    private string? _backgroundProgress;

    /// <summary>What background describing is doing ("Describing photos in the background, 3 of 40"), shown in the status bar.</summary>
    public string? BackgroundProgress
    {
        get => _backgroundProgress;
        set { _backgroundProgress = value; OnPropertyChanged(); OnStatusChanged(); }
    }

    /// <summary>Takes a folder out of Photo Finder's library. Nothing on disk changes.</summary>
    public async Task RemoveFolderAsync(string root)
    {
        if (_settings.ExtraFolders.RemoveAll(f => f.Equals(root, StringComparison.OrdinalIgnoreCase)) == 0)
            _settings.RemovedFolders.Add(root);
        SaveSettings();
        // Leave the folder first, so the rescan doesn't report it as having gone from the library by itself.
        var inside = CurrentRoot is { } current && current.Equals(root, StringComparison.OrdinalIgnoreCase);
        if (inside) Navigate(SidebarIds.AllPhotos, announce: false);
        await RescanAsync();
        Announcer.Announce(inside
            ? $"Removed {Path.GetFileName(root)} from {AppInfo.Name}. Showing All Photos, {CountText(Rows.Count)}"
            : $"Removed {Path.GetFileName(root)} from {AppInfo.Name}");
    }

    /// <summary>The library folder the current place is in, if it's a folder.</summary>
    public string? CurrentRoot =>
        CurrentSource?.FolderPath is { } path
            ? Roots.FirstOrDefault(r => r.Equals(path, StringComparison.OrdinalIgnoreCase) || LibraryScanner.IsInside(path, r))
            : null;

    /// <summary>Watches the library's folders. Done off the UI thread: checking a disconnected network
    /// folder can take many seconds.</summary>
    private async Task WatchRootsAsync()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var roots = Roots.ToList();
        var generation = _scanGeneration;
        var made = await Task.Run(() => MakeWatchers(roots));
        // A newer scan (or closing) happened meanwhile: these are already out of date.
        if (_disposed || generation != _scanGeneration)
        {
            foreach (var w in made) w.Dispose();
            return;
        }
        _watchers.AddRange(made);
    }

    private List<FileSystemWatcher> MakeWatchers(List<string> roots)
    {
        var watchers = new List<FileSystemWatcher>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnDiskChange;
                watcher.Deleted += OnDiskChange;
                watcher.Renamed += OnDiskChange;
                watcher.Changed += OnDiskChange;
                watcher.Error += (_, _) => OnDiskChange(this, new FileSystemEventArgs(WatcherChangeTypes.All, root, null));
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // A folder that can't be watched is still listed; F5 refreshes it.
            }
        }
        return watchers;
    }

    private SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>Files changed on disk: list them again once things have been quiet for a moment.</summary>
    private void OnDiskChange(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreChangesUntil) return;
        if (e.ChangeType != WatcherChangeTypes.All && e.Name is { } name && Path.HasExtension(name) && !MediaTypes.IsMedia(name)) return;
        _ui ??= SynchronizationContext.Current;
        _ui?.Post(_ =>
        {
            _rescanDebounce?.Cancel();
            var cancel = _rescanDebounce = new CancellationTokenSource();
            _ = Task.Delay(1500, cancel.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Fire(RescanAsync());
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }, null);
    }

    // MARK: Gallery

    /// <summary>The item the gallery shows: the first selected row in list order. Worked out once per
    /// selection change, since a big selection in a big list makes it costly.</summary>
    public FileRow? FocusedRow => FocusedIndex >= 0 && FocusedIndex < Rows.Count ? Rows[FocusedIndex] : null;

    private int FocusedIndex { get; set; } = -1;

    private void UpdateFocusedRow()
    {
        FocusedIndex = -1;
        if (_selectedRows.Count == 0) return;
        if (_selectedRows.Count == 1)
        {
            FocusedIndex = Rows.IndexOf(_selectedRows[0]);
            return;
        }
        var set = new HashSet<FileRow>(_selectedRows);
        FocusedIndex = Rows.FindIndex(set.Contains);
    }

    public (int Index, int Count)? GalleryPosition => FocusedRow is null ? null : (FocusedIndex, Rows.Count);

    public void EnsureGallerySelection()
    {
        if (FocusedRow is null && Rows.Count > 0) SelectionRequested?.Invoke([Rows[0].Id], false);
    }

    public void MoveSelection(int delta)
    {
        if (Rows.Count == 0) { Beep(); return; }
        var current = FocusedIndex;
        var next = current < 0 ? 0 : current + delta;
        if (next < 0 || next >= Rows.Count) { Beep(); return; }
        SelectionRequested?.Invoke([Rows[next].Id], false);
        Announcer.Announce($"{Rows[next].Name}, {next + 1} of {Rows.Count}");
    }

    // MARK: Helpers

    public static string CountText(int count) => count == 1 ? "1 item" : $"{count:N0} items";

    private void RaiseError(string message) => ErrorRaised?.Invoke(message);

    /// <summary>Runs a task nobody waits for, reporting rather than losing what goes wrong.</summary>
    public void Fire(Task task) => task.ContinueWith(t => RaiseError($"Something went wrong: {t.Exception?.GetBaseException().Message}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
        SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());

    private void SaveCacheInBackground() => Task.Run(_cache.Save);

    private static void Beep() => System.Media.SystemSounds.Beep.Play();

    public void SaveSettings() => _settings.Save(_settingsPath);

    /// <summary>Saves the photo details and descriptions now, as closing does: before an update closes Photo Finder.</summary>
    public void SaveCaches()
    {
        _cache.Save();
        Descriptions?.Save();
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CurrentActivity));
        OnPropertyChanged(nameof(IsBusy));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _fillCancel?.Cancel();
        _rescanDebounce?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _cache.Save();
    }
}
