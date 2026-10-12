using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Describes the library's photos behind the scenes (a setting, off until turned on), so their descriptions are ready
/// when you arrow to them, spoken after the usual short pause without waiting for Windows AI (#96).
///
/// - It works through every photo in the library not described yet, newest first: when it's turned on, each time
///   Photo Finder starts (so a big library carries on across restarts), and for a folder as it's added.
/// - One photo at a time, into the same store automatic descriptions and Get Info use.
/// - Photos are queued by path and looked up again when their turn comes, so a folder removed meanwhile is
///   skipped and a photo Photo Finder edited is described under its current details.
/// - Arrowing comes first: a photo isn't started while automatic descriptions are describing or about to.
/// - Already described photos, videos, and online-only cloud photos (describing would download them) are skipped.
/// - Progress is in the status bar, finishing a folder is announced, and Photo > Stop Background Describing stops it.
/// </summary>
public sealed class BackgroundDescriber : IDisposable
{
    private readonly LibraryModel _model;
    private readonly DescriptionStore _store;
    private readonly AutoDescriber.DescribeFunc _describe;
    private readonly Func<CancellationToken, Task<AIReadiness>> _check;
    private readonly Func<CancellationToken, Task<AIReadiness>> _prepare;
    private readonly Queue<(string Path, string Folder)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _perFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();
    private Task _running = Task.CompletedTask;
    private int _total;
    private int _done;

    public BackgroundDescriber(LibraryModel model, DescriptionStore store, AutoDescriber.DescribeFunc describe,
        Func<CancellationToken, Task<AIReadiness>> check, Func<CancellationToken, Task<AIReadiness>> prepare)
    {
        _model = model;
        _store = store;
        _describe = describe;
        _check = check;
        _prepare = prepare;
        _model.FolderAdded += (folder, paths) => Busy(FolderAddedAsync(folder, paths));
    }

    /// <summary>True while automatic descriptions are describing, or about to: arrowing comes first.</summary>
    public Func<bool> ForegroundBusy { get; set; } = () => false;

    public bool IsRunning => !_running.IsCompleted;

    private readonly List<Task> _busy = [];

    private void Busy(Task task)
    {
        _busy.RemoveAll(t => t.IsCompleted);
        _busy.Add(task);
    }

    /// <summary>For tests: finishes once nothing is being checked, queued or described.</summary>
    internal async Task WhenIdleAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((IsRunning || _busy.Any(t => !t.IsCompleted)) && DateTime.UtcNow < until) await Task.Delay(10);
    }

    public bool Enabled
    {
        get => _model.Settings.DescribeInBackground;
        set
        {
            _model.Settings.DescribeInBackground = value;
            _model.SaveSettings();
            if (!value)
            {
                var stopped = StopQueue();
                Announcer.Announce(stopped > 0
                    ? $"Background describing off. Stopped with {Plural(stopped)} not described."
                    : "Photos won't be described in the background");
                return;
            }
            Announcer.Announce("Photos in the library will be described in the background");
            Busy(TurnedOnAsync());
        }
    }

    /// <summary>Photo > Stop Background Describing: stops this run, leaving the setting on for folders added later.</summary>
    public void Stop()
    {
        var stopped = StopQueue();
        Announcer.Announce(stopped > 0 ? $"Stopped describing in the background. {Plural(stopped)} not described." : "Nothing is being described in the background");
    }

    /// <summary>Empties the queue (the photo being described finishes) and returns how many were left.</summary>
    private int StopQueue()
    {
        var left = _queue.Count;
        _queue.Clear();
        _queued.Clear();
        _perFolder.Clear();
        _announceLibrary = false;
        if (!IsRunning) Finish();
        return left;
    }

    private async Task TurnedOnAsync()
    {
        if (await CheckWhenTurnedOnAsync()) await DescribeLibraryAsync(announce: true);
    }

    /// <summary>The library's photos not described yet, newest first, queued: when it's turned on, and when Photo Finder
    /// starts. Said aloud only when the person turned it on; at a start the status bar shows it.</summary>
    public async Task DescribeLibraryAsync(bool announce = false)
    {
        if (!Enabled) return;
        AIReadiness readiness;
        try { readiness = await _check(_closing.Token); }
        catch (Exception) { return; }
        if (readiness.State != "Ready" || !Enabled) return;
        var kind = _model.Settings.DescriptionKind;
        var todo = _model.AllItems
            .Where(i => !i.IsVideo && !i.IsOnlineOnly && MediaTypes.KindOf(i.Path) == MediaKind.Image && !_queued.Contains(i.Path) && _store.Get(i, kind) is null)
            .OrderByDescending(i => i.Metadata?.DateTaken ?? i.Modified)
            .Select(i => i.Path)
            .ToList();
        if (todo.Count == 0) return;
        foreach (var path in todo)
        {
            _queue.Enqueue((path, LibraryKey));
            _queued.Add(path);
        }
        _perFolder[LibraryKey] = _perFolder.GetValueOrDefault(LibraryKey) + todo.Count;
        _total += todo.Count;
        _announceLibrary |= announce;
        if (announce) Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (!IsRunning) _running = RunAsync();
    }

    /// <summary>The queue's name for the library as a whole, rather than a folder that was added.</summary>
    private const string LibraryKey = "";
    private bool _announceLibrary;

    /// <summary>Turned on by the person: say now if it can't work, or get the model ready if that's all it needs. True
    /// when Windows AI is ready.</summary>
    private async Task<bool> CheckWhenTurnedOnAsync()
    {
        try
        {
            var readiness = await _check(_closing.Token);
            if (readiness.State == "NotReady")
            {
                Announcer.Announce("Getting Windows' image description model ready. The first time, this can take a few minutes.");
                readiness = await _prepare(_closing.Token);
            }
            if (readiness.State != "Ready")
            {
                Announcer.Announce($"Photos can't be described in the background. {readiness.Message}");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Closing.
            return false;
        }
        catch (Exception ex)
        {
            Announcer.Announce($"Photos can't be described in the background. {ex.Message}");
            return false;
        }
    }

    private async Task FolderAddedAsync(string folder, IReadOnlyList<string> paths)
    {
        if (!Enabled) return;
        AIReadiness readiness;
        try { readiness = await _check(_closing.Token); }
        catch (Exception) { return; }
        // Turning the setting on already said why it can't work; adding a folder shouldn't promise anything.
        if (readiness.State != "Ready") return;

        var kind = _model.Settings.DescriptionKind;
        var items = _model.AllItems.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var todo = paths
            .Where(p => MediaTypes.KindOf(p) == MediaKind.Image && !_queued.Contains(p))
            .Where(p => !items.TryGetValue(p, out var item) || (!item.IsOnlineOnly && _store.Get(item, kind) is null))
            .ToList();
        if (todo.Count == 0) return;
        foreach (var path in todo)
        {
            _queue.Enqueue((path, folder));
            _queued.Add(path);
        }
        _perFolder[folder] = _perFolder.GetValueOrDefault(folder) + todo.Count;
        _total += todo.Count;
        Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} from {DisplayName(folder)} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (!IsRunning) _running = RunAsync();
    }

    private async Task RunAsync()
    {
        while (_queue.Count > 0 && !_closing.IsCancellationRequested)
        {
            // Arrowing comes first: wait while automatic descriptions are describing or about to.
            while (ForegroundBusy() && !_closing.IsCancellationRequested)
            {
                try { await Task.Delay(250, _closing.Token); }
                catch (OperationCanceledException) { return; }
            }
            if (_queue.Count == 0) break;
            var (path, folder) = _queue.Dequeue();
            _queued.Remove(path);
            ShowProgress();
            // Looked up now, not when queued: the folder may have been removed, or the photo edited.
            var item = _model.AllItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            var kind = _model.Settings.DescriptionKind;
            if (item is { IsVideo: false, IsOnlineOnly: false } && _store.Get(item, kind) is null)
            {
                try
                {
                    var text = await _describe(item, kind, _closing.Token);
                    if (text.Length > 0) _store.PutUnlessEdited(item, kind, text);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // One photo that can't be described (filtered, unreadable, timed out) doesn't stop the rest.
                }
            }
            _done++;
            if (_perFolder.ContainsKey(folder) && --_perFolder[folder] == 0)
            {
                _perFolder.Remove(folder);
                if (folder == LibraryKey)
                {
                    // The whole library: said when the person started it, otherwise the status bar just clears.
                    if (_announceLibrary) Announcer.AnnounceAfter("Finished describing the library's photos", "PhotoFinderBackground");
                    _announceLibrary = false;
                }
                else if (_model.Roots.Any(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase) || LibraryScanner.IsInside(folder, r)))
                    Announcer.AnnounceAfter($"Finished describing the photos in {DisplayName(folder)}", "PhotoFinderBackground");
            }
        }
        Finish();
    }

    private void ShowProgress() =>
        _model.BackgroundProgress = $"Describing photos in the background, {Math.Min(_done + 1, Math.Max(_total, 1)):N0} of {_total:N0}";

    private void Finish()
    {
        _total = 0;
        _done = 0;
        _model.BackgroundProgress = null;
    }

    private static string DisplayName(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    private static string Plural(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";

    public void Dispose()
    {
        _closing.Cancel();
        _store.Save();
    }
}
