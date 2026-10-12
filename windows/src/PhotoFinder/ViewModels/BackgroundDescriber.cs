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
    private readonly Queue<(string Path, string Folder, int Run)> _queue = new();
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

    /// <summary>Photo > Stop Background Describing: stops this run. The setting stays on, so folders added later are
    /// described, and the next start carries on with the library.</summary>
    public void Stop()
    {
        var stopped = StopQueue();
        Announcer.Announce(stopped > 0
            ? $"Stopped describing in the background. {Plural(stopped)} not described; it carries on the next time {AppInfo.Name} starts."
            : "Nothing is being described in the background");
    }

    /// <summary>Empties the queues (the photo being described finishes, but no longer counts) and returns how many were
    /// left.</summary>
    private int StopQueue()
    {
        var left = _front.Count + _queue.Count;
        _front.Clear();
        _queue.Clear();
        _queued.Clear();
        _perFolder.Clear();
        _announceLibrary = false;
        // A new run: the photo still being described belongs to the old one, so it isn't counted again.
        _run++;
        // The status bar clears now, even while the photo already being described finishes.
        Finish();
        return left;
    }

    private async Task TurnedOnAsync()
    {
        if (await CheckWhenTurnedOnAsync()) await DescribeLibraryAsync(announce: true);
    }

    /// <summary>At a start: the library's photos not described yet, quietly, tracked so tests can wait for it.</summary>
    public void Resume() => Busy(DescribeLibraryAsync());

    /// <summary>The library's photos not described yet, newest first, queued: when it's turned on, and when Photo Finder
    /// starts. Said aloud only when the person turned it on; at a start the status bar shows it. "Newest" is the date
    /// taken when the details have been read, and otherwise the file's modified date.</summary>
    public async Task DescribeLibraryAsync(bool announce = false)
    {
        if (!Enabled) return;
        AIReadiness readiness;
        try { readiness = await _check(_closing.Token); }
        catch (Exception) { return; }
        if (readiness.State != "Ready" || !Enabled) return;
        var kind = _model.Settings.DescriptionKind;
        var todo = _model.AllItems
            .Where(i => !i.IsVideo && !i.IsOnlineOnly && MediaTypes.KindOf(i.Path) == MediaKind.Image && Wanted(i.Path) && _store.Get(i, kind) is null
                        && !_failed.Contains(FailureKey(i)))
            .OrderByDescending(i => i.Metadata?.DateTaken ?? i.Modified)
            .Select(i => i.Path)
            .ToList();
        if (todo.Count == 0) return;
        foreach (var path in todo)
        {
            _queue.Enqueue((path, LibraryKey, _run));
            _queued.Add(path);
        }
        _perFolder[LibraryKey] = _perFolder.GetValueOrDefault(LibraryKey) + todo.Count;
        _total += todo.Count;
        _announceLibrary |= announce;
        if (announce) Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (!IsRunning) _running = RunAsync();
    }

    /// <summary>Not queued already, and not the photo being described now.</summary>
    private bool Wanted(string path) => !_queued.Contains(path) && !string.Equals(path, _current, StringComparison.OrdinalIgnoreCase);

    /// <summary>The queue's name for the library as a whole, rather than a folder that was added.</summary>
    private const string LibraryKey = "";
    private bool _announceLibrary;

    /// <summary>Folders as they're added, described before the library's backlog.</summary>
    private readonly Queue<(string Path, string Folder, int Run)> _front = new();
    /// <summary>Which run the queues belong to: Stop starts a new one, so an older photo finishing isn't counted.</summary>
    private int _run;
    /// <summary>The photo being described now.</summary>
    private string? _current;

    /// <summary>Photos that couldn't be described this session, by path, size and date, so a new run doesn't try them
    /// again.</summary>
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private static string FailureKey(MediaItem item) => $"{item.Path}|{item.Size}|{item.Modified.Ticks}";

    /// <summary>After this many photos in a row fail, the run stops and says why, rather than failing through thousands
    /// of photos (a missing HEIF codec, a broken helper).</summary>
    internal const int FailuresInARow = 5;

    /// <summary>Whether it's a good time to work: not on energy saver. Set by the app; it waits meanwhile.</summary>
    public Func<bool> MayRun { get; set; } = () => true;

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
            .Where(p => MediaTypes.KindOf(p) == MediaKind.Image && !string.Equals(p, _current, StringComparison.OrdinalIgnoreCase))
            .Where(p => !items.TryGetValue(p, out var item) || (!item.IsOnlineOnly && _store.Get(item, kind) is null))
            .ToList();
        if (todo.Count == 0) return;
        // Moved ahead of the library's backlog: one already waiting there is taken out of it, and counted for the folder.
        var moved = todo.Where(_queued.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moved.Count > 0)
        {
            var rest = _queue.Where(q => !moved.Contains(q.Path)).ToList();
            var fromLibrary = _queue.Count - rest.Count;
            _queue.Clear();
            foreach (var q in rest) _queue.Enqueue(q);
            if (_perFolder.ContainsKey(LibraryKey) && (_perFolder[LibraryKey] -= fromLibrary) <= 0) _perFolder.Remove(LibraryKey);
            _total -= fromLibrary;
        }
        foreach (var path in todo.Where(p => !_front.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))))
        {
            _front.Enqueue((path, folder, _run));
            _queued.Add(path);
            _perFolder[folder] = _perFolder.GetValueOrDefault(folder) + 1;
            _total++;
        }
        Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} from {DisplayName(folder)} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (!IsRunning) _running = RunAsync();
    }

    private async Task RunAsync()
    {
        var failedInARow = 0;
        var lastRun = _run;
        while (_front.Count + _queue.Count > 0 && !_closing.IsCancellationRequested)
        {
            // Arrowing comes first: wait while automatic descriptions are describing or about to. And not on energy saver.
            // Stop empties the queues, which ends the wait.
            while ((ForegroundBusy() || !MayRun()) && _front.Count + _queue.Count > 0 && !_closing.IsCancellationRequested)
            {
                if (!MayRun()) _model.BackgroundProgress = "Background describing paused on energy saver";
                try { await Task.Delay(250, _closing.Token); }
                catch (OperationCanceledException) { return; }
            }
            if (_front.Count + _queue.Count == 0) break;
            var (path, folder, run) = _front.Count > 0 ? _front.Dequeue() : _queue.Dequeue();
            // A new run (after Stop, or turning it off and on) counts failures afresh.
            if (run != lastRun) (failedInARow, lastRun) = (0, run);
            _queued.Remove(path);
            ShowProgress();
            // Looked up now, not when queued: the folder may have been removed, or the photo edited.
            var item = _model.AllItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            var kind = _model.Settings.DescriptionKind;
            if (item is { IsVideo: false, IsOnlineOnly: false } && _store.Get(item, kind) is null)
            {
                _current = path;
                try
                {
                    var text = await _describe(item, kind, _closing.Token);
                    if (text.Length > 0) _store.PutUnlessEdited(item, kind, text);
                    failedInARow = 0;
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One photo that can't be described (filtered, unreadable, timed out) doesn't stop the rest, but many
                    // in a row means something's wrong with every photo: stop and say what, once.
                    _failed.Add(FailureKey(item));
                    if (run == _run && ++failedInARow >= FailuresInARow)
                    {
                        var left = StopQueue();
                        Announcer.AnnounceAfter(
                            $"Background describing stopped: {Plural(FailuresInARow)} in a row couldn't be described, so {Plural(left)} weren't tried. " +
                            (ex is DescriptionException ? ex.Message : $"Windows couldn't describe them: {ex.Message}"), "PhotoFinderBackground");
                        break;
                    }
                }
                finally
                {
                    _current = null;
                }
            }
            // Stop or turning it off started a new run meanwhile: this photo belonged to the old one.
            if (run != _run) continue;
            _done++;
            if (_perFolder.ContainsKey(folder) && --_perFolder[folder] == 0)
            {
                _perFolder.Remove(folder);
                if (folder != LibraryKey && _model.Roots.Any(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase) || LibraryScanner.IsInside(folder, r)))
                    Announcer.AnnounceAfter($"Finished describing the photos in {DisplayName(folder)}", "PhotoFinderBackground");
            }
            // The whole library: said when the person started it, once none of it is left, including photos moved ahead
            // for a folder added meanwhile. Otherwise the status bar just clears.
            if (_announceLibrary && !_perFolder.ContainsKey(LibraryKey) && _front.Count == 0)
            {
                Announcer.AnnounceAfter("Finished describing the library's photos", "PhotoFinderBackground");
                _announceLibrary = false;
            }
        }
        Finish();
    }

    private void ShowProgress()
    {
        if (_total > 0) _model.BackgroundProgress = $"Describing photos in the background, {Math.Min(_done + 1, _total):N0} of {_total:N0}";
    }

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
