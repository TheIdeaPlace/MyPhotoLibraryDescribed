using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Describes the library's photos behind the scenes (a setting, off until turned on), so their descriptions are ready
/// when you arrow to them, spoken after the usual short pause without waiting for Windows AI (#96).
///
/// - It works through every photo in the library not described yet, newest first: when it's turned on, each time
///   My Photo Library Described starts (so a big library carries on across restarts), and for a folder as it's added.
/// - One photo at a time, into the same store automatic descriptions and Get Info use.
/// - Photos are queued by path and looked up again when their turn comes, so a folder removed meanwhile is
///   skipped and a photo My Photo Library Described edited is described under its current details.
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

    /// <param name="failuresPath">Where the photos that couldn't be described are remembered between starts; none in
    /// tests unless given.</param>
    public BackgroundDescriber(LibraryModel model, DescriptionStore store, AutoDescriber.DescribeFunc describe,
        Func<CancellationToken, Task<AIReadiness>> check, Func<CancellationToken, Task<AIReadiness>> prepare, string? failuresPath = null)
    {
        _model = model;
        _store = store;
        _describe = describe;
        _check = check;
        _prepare = prepare;
        _failuresPath = failuresPath;
        LoadFailures();
        _resume = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
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

    /// <summary>For tests: finishes once nothing is being checked, queued or described. Waiting to ask Windows AI again
    /// counts as idle, since that goes on until it answers.</summary>
    internal async Task WhenIdleAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((IsRunning || (_busy.Any(t => !t.IsCompleted) && !(_waitingForAI && _asking == 0))) && DateTime.UtcNow < until) await Task.Delay(10);
    }

    /// <summary>For tests: a check of Windows AI is under way.</summary>
    private int _asking;

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
            // Turned on by the person: everything is tried again, including photos that couldn't be described before
            // (a codec installed since, say).
            ForgetFailures();
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
        // Failures not yet shown to be the photos' own aren't kept once the run ends early, however it was stopped.
        _pending.Clear();
        _pendingTimeouts.Clear();
        _front.Clear();
        _queue.Clear();
        _queued.Clear();
        _perFolder.Clear();
        _announceLibrary = false;
        // A new run: the photo still being described belongs to the old one, so it isn't counted again.
        _run++;
        // And no more asking Windows AI again from the start, nor starting when a description works: Stop means until the
        // next start.
        CancelResume();
        _waitingForAI = false;
        // The status bar clears now, even while the photo already being described finishes.
        Finish();
        return left;
    }

    private async Task TurnedOnAsync()
    {
        // Asks for itself, and says why if it can't.
        CancelResume();
        _waitingForAI = false;
        if (await CheckWhenTurnedOnAsync()) await DescribeLibraryAsync(announce: true);
    }

    /// <summary>At a start: the library's photos not described yet, quietly, tracked so tests can wait for it.</summary>
    public void Resume()
    {
        _waitingForAI = false;
        Busy(ResumeAsync(_resume.Token));
    }

    /// <summary>Gave up waiting for Windows AI at a start: the next description that works anywhere (Describe Photo, say)
    /// starts it, with nothing for the person to do.</summary>
    private bool _waitingForAI;

    /// <summary>Windows AI has just described a photo: if background describing was waiting for it, it starts now.</summary>
    public void WindowsAIWorked()
    {
        if (!_waitingForAI || !Enabled || IsRunning) return;
        Log.Write("background", "Windows AI is working again; background describing starts");
        CancelResume();
        Resume();
    }

    /// <summary>How long to wait before asking Windows AI again when it couldn't answer at a start, then every
    /// <see cref="ResumePoll"/> after that. Tests shorten them.</summary>
    internal TimeSpan[] ResumeRetries { get; set; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    internal TimeSpan ResumePoll { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Said while background describing waits for Windows AI to answer.</summary>
    internal const string WaitingText = "Background describing is waiting for Windows AI. It starts by itself once Windows AI is working.";

    /// <summary>Asking again from a start; cancelled by Stop, turning it off or on, and closing.</summary>
    private CancellationTokenSource _resume;

    private void CancelResume()
    {
        _resume.Cancel();
        _resume.Dispose();
        _resume = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
    }

    /// <summary>Windows AI can be slow to answer just after a start or signing in (the helper's check timing out, say), so
    /// a check that fails is asked again, soon and then every ten minutes, rather than waiting for the next start;
    /// meanwhile the status bar says it's waiting, without a busy indicator, and the first description that works anywhere
    /// starts it at once. When Windows AI can't describe on this PC at all, the status bar says why and it stops asking.</summary>
    private async Task ResumeAsync(CancellationToken cancel)
    {
        var tries = 0;
        while (!cancel.IsCancellationRequested)
        {
            var (tryAgain, problem) = await DescribeLibraryAsync(cancel: cancel);
            if (cancel.IsCancellationRequested) return;
            if (!tryAgain)
            {
                _waitingForAI = false;
                if (problem is not null && Enabled && !IsRunning)
                {
                    Log.Write("background", $"Not describing: {problem}");
                    _model.ShowBackgroundProblem(problem);
                }
                return;
            }
            if (!Enabled) return;
            if (!IsRunning)
            {
                if (!_waitingForAI) Log.Write("background", "Waiting for Windows AI; asking again soon, then every ten minutes");
                _model.ShowBackgroundProblem(WaitingText);
                _waitingForAI = true;
            }
            var wait = tries < ResumeRetries.Length ? ResumeRetries[tries] : ResumePoll;
            tries++;
            try { await Task.Delay(wait, cancel); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The library's photos not described yet, newest first, queued: when it's turned on, and when My Photo Library Described
    /// starts. Said aloud only when the person turned it on; at a start the status bar shows it. "Newest" is the date
    /// taken when the details have been read, and otherwise the file's modified date. Returns whether it's worth asking
    /// Windows AI again (it didn't answer), and why it can't describe, if it can't.</summary>
    internal async Task<(bool TryAgain, string? Problem)> DescribeLibraryAsync(bool announce = false, CancellationToken cancel = default)
    {
        if (!Enabled) return (false, null);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancel);
        AIReadiness readiness;
        _asking++;
        try
        {
            readiness = await _check(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return (false, null);
        }
        catch (Exception ex)
        {
            Log.Error("background", "Couldn't ask Windows AI whether it's ready", ex);
            return (true, null);
        }
        finally
        {
            _asking--;
        }
        if (!Enabled || linked.IsCancellationRequested) return (false, null);
        if (readiness.State == "NotReady")
        {
            // The model's gone since it was turned on (a Windows update, say): got ready again, saying so, rather than
            // waiting for the person to do something. The first time it can take a few minutes.
            Log.Write("background", "Windows AI's model isn't ready; getting it ready");
            if (!IsRunning) _model.BackgroundProgress = "Getting Windows' image description model ready for background describing";
            try
            {
                readiness = await _prepare(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return (false, null);
            }
            catch (Exception ex)
            {
                Log.Error("background", "Windows AI's model couldn't be got ready", ex);
                readiness = new AIReadiness(true, "Error", ex.Message);
            }
            finally
            {
                if (!IsRunning) _model.BackgroundProgress = null;
            }
            if (!Enabled || linked.IsCancellationRequested) return (false, null);
            // Not ready yet (still downloading, say): asked again later, as for a helper that didn't answer.
            if (readiness.State == "NotReady") readiness = readiness with { State = "Error" };
        }
        if (readiness.State != "Ready")
        {
            Log.Write("background", $"Not started: Windows AI is {readiness.State}. {readiness.Message}");
            return readiness.State switch
            {
                // The helper not starting or not answering in time, which can pass.
                "Error" => (true, null),
                _ => (false, $"Background describing can't run. {readiness.Message}"),
            };
        }
        var kind = _model.Settings.DescriptionKind;
        var undescribed = _model.AllItems
            .Where(i => !i.IsVideo && !i.IsOnlineOnly && MediaTypes.KindOf(i.Path) == MediaKind.Image && Wanted(i.Path) && _store.Get(i, kind) is null)
            .ToList();
        var todo = undescribed
            .Where(i => !_failed.Contains(FailureKey(i, kind)))
            .OrderByDescending(i => i.Metadata?.DateTaken ?? i.Modified)
            .Select(i => i.Path)
            .ToList();
        if (undescribed.Count > todo.Count)
            Log.Write("background", $"Skipped {undescribed.Count - todo.Count} photos that couldn't be described before");
        if (todo.Count == 0)
        {
            if (!announce) Log.Write("background", "Nothing in the library left to describe");
            return (false, null);
        }
        foreach (var path in todo)
        {
            _queue.Enqueue((path, LibraryKey, _run));
            _queued.Add(path);
        }
        _perFolder[LibraryKey] = _perFolder.GetValueOrDefault(LibraryKey) + todo.Count;
        _total += todo.Count;
        _announceLibrary |= announce;
        Log.Write("background", $"Queued {todo.Count} photos from the library ({(announce ? "turned on" : "at start")})");
        if (announce) Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (!IsRunning) _running = RunAsync();
        return (false, null);
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

    /// <summary>Photos that couldn't be described, by path, size and date, so a new run doesn't try them again: not this
    /// session, and not after a restart either, or the same few photos at the front of the queue (newest first) would
    /// fail again at every start and stop the run before it reached the rest (Kelly, 13 Oct). Kept per version of the app:
    /// an update may describe them, so they're tried once more then. An edited photo has a new date, so it's tried too.</summary>
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The failures kept for later starts: the photo's own verdicts at once, other failures only once a later
    /// photo in the same run was described, proving describing works. A run that stops after failures in a row keeps
    /// none of its streak, so a problem with every photo (a missing codec, a broken helper) can't leave good photos
    /// skipped once it's fixed.</summary>
    private readonly HashSet<string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = [];
    /// <summary>Photos that timed out, kept for later starts only once a later photo is described quickly: a slow spell
    /// (the NPU busy with something else) times photos out too, and those shouldn't be skipped for good.</summary>
    private readonly List<string> _pendingTimeouts = [];

    /// <summary>A description this quick shows Windows was answering normally, so the photos that timed out before it
    /// were the photos' own trouble.</summary>
    internal static TimeSpan QuickAnswer { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The photo by path, size and modified time (UTC, so a daylight saving change doesn't lose them all), and the
    /// description type: one the filter declined as Detailed may be fine as Brief.</summary>
    private static string FailureKey(MediaItem item, string kind) => $"{item.Path}|{item.Size}|{item.Modified.ToUniversalTime().Ticks}|{kind}";
    private readonly string? _failuresPath;

    private sealed record Failures(string Version, List<string>? Keys);

    public static string DefaultFailuresPath => Path.Combine(AppPaths.DataFolder, "BackgroundFailures.json");

    private void LoadFailures()
    {
        if (_failuresPath is null || !File.Exists(_failuresPath)) return;
        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<Failures>(File.ReadAllText(_failuresPath));
            if (saved?.Version != Updates.AppVersion) return;
            var keys = (saved.Keys ?? []).Where(k => !string.IsNullOrEmpty(k)).ToList();
            _remembered.UnionWith(keys);
            _failed.UnionWith(keys);
        }
        catch (Exception ex)
        {
            // Never a reason not to start: they're just tried again.
            Log.Write("background", $"The photos that couldn't be described before weren't read ({ex.GetType().Name}); they'll be tried again");
        }
    }

    private void SaveFailures()
    {
        if (_failuresPath is null) return;
        try
        {
            // Photos no longer in the library are dropped, so the file doesn't grow forever. (One on a share that's offline
            // just now is dropped too, and simply tried again later.)
            var paths = _model.AllItems.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _remembered.RemoveWhere(k => !paths.Contains(k[..Math.Max(0, k.IndexOf('|'))]));
            Directory.CreateDirectory(Path.GetDirectoryName(_failuresPath)!);
            var temp = _failuresPath + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(new Failures(Updates.AppVersion, [.. _remembered])));
            File.Move(temp, _failuresPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("background", $"The photos that couldn't be described weren't saved ({ex.GetType().Name}); they'll be tried again next start");
        }
    }

    private void ForgetFailures()
    {
        _failed.Clear();
        _pending.Clear();
        _pendingTimeouts.Clear();
        if (_remembered.Count == 0) return;
        _remembered.Clear();
        SaveFailures();
    }

    /// <summary>Windows' final verdict on the photo itself, not a sign that describing doesn't work: its content filter
    /// declining what's in the picture, or too much text in it to describe. These don't count towards stopping the run.
    /// A missing codec shows as decode_failed or unsupported_format, which do.</summary>
    internal static bool IsAboutThePhoto(Exception ex) => ex is DescriptionException { Code: "content_filtered" or "too_much_text" };

    /// <summary>A failure that may well not happen next time: the helper stopping partway (WindowsAI says "stopped
    /// unexpectedly"). Never kept for later starts. A timeout isn't one: Windows' model can hang on a particular photo
    /// every time (Kelly's PC, 19 Oct), and that photo, first in the queue, held every start up. So a timeout is kept
    /// like other failures, once a later photo in the run is described, which shows Windows wasn't simply slow.</summary>
    internal static bool IsPassing(Exception ex) =>
        ex is DescriptionException { Code: "internal_error" } d && d.Message.Contains("stopped unexpectedly", StringComparison.Ordinal);

    internal static bool IsTimeout(Exception ex) => ex is TimeoutException or DescriptionException { Code: "timeout" };

    /// <summary>The pause after a photo times out before the next is sent: Windows' AI service stays slow for a while after
    /// a hung request, and the next photo sent straight away tends to time out too. Settable for tests.</summary>
    internal static TimeSpan AfterTimeoutPause { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>After this many photos in a row fail, the run stops and says why, rather than failing through thousands
    /// of photos (a missing HEIF codec, a broken helper). Photos Windows' content filter declines don't count.</summary>
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
        catch (Exception ex)
        {
            if (!_closing.IsCancellationRequested) Log.Error("background", "Couldn't ask Windows AI whether it's ready for an added folder", ex);
            return;
        }
        // Turning the setting on already said why it can't work; adding a folder shouldn't promise anything.
        if (readiness.State != "Ready")
        {
            Log.Write("background", $"An added folder wasn't queued: Windows AI is {readiness.State}. {readiness.Message}");
            return;
        }

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
                    var (text, took) = await _describe(item, kind, _closing.Token);
                    if (text.Length > 0) _store.PutUnlessEdited(item, kind, text, took);
                    failedInARow = 0;
                    // Describing works, so the failures before this one were the photos' own: kept for later starts. Timeouts
                    // only when this one was quick, so Windows wasn't just slow for a while.
                    var quick = took is not { } time || time < QuickAnswer;
                    if (_pending.Count > 0 || (quick && _pendingTimeouts.Count > 0))
                    {
                        _remembered.UnionWith(_pending);
                        _pending.Clear();
                        if (quick)
                        {
                            _remembered.UnionWith(_pendingTimeouts);
                            _pendingTimeouts.Clear();
                        }
                        SaveFailures();
                    }
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One photo that can't be described (filtered, unreadable, timed out) doesn't stop the rest, and is
                    // remembered so it isn't tried at every start; but many in a row means something's wrong with every
                    // photo: stop and say what, once.
                    var key = FailureKey(item, kind);
                    _failed.Add(key);
                    if (IsAboutThePhoto(ex))
                    {
                        _remembered.Add(key);
                        SaveFailures();
                    }
                    // The helper stopping partway says nothing about the photo: skipped this session only.
                    else if (IsTimeout(ex)) _pendingTimeouts.Add(key);
                    else if (!IsPassing(ex)) _pending.Add(key);
                    Log.Write("background", $"Couldn't describe {item.Name}: {(ex is DescriptionException d ? $"{d.Code}: " : ex.GetType().Name + ": ")}{ex.Message}");
                    // A pause before the next photo, only while this run goes on: not after Stop or turning it off, not
                    // with nothing left, and not when this failure ends the run.
                    if (IsTimeout(ex) && run == _run && failedInARow + 1 < FailuresInARow && _front.Count + _queue.Count > 0)
                    {
                        _model.BackgroundProgress = $"Windows got stuck on {item.Name}; going on in a moment";
                        var pause = System.Diagnostics.Stopwatch.StartNew();
                        while (pause.Elapsed < AfterTimeoutPause && run == _run && _front.Count + _queue.Count > 0)
                        {
                            try { await Task.Delay(TimeSpan.FromMilliseconds(250), _closing.Token); }
                            catch (OperationCanceledException) { return; }
                        }
                        // Stopped meanwhile: Stop has already said so and cleared the progress.
                        if (run == _run) ShowProgress();
                    }
                    if (run == _run && !IsAboutThePhoto(ex) && ++failedInARow >= FailuresInARow)
                    {
                        // Something may be wrong with every photo: StopQueue drops the streak, so it isn't kept for later starts.
                        var left = StopQueue();
                        Log.Write("background", $"Stopped after {FailuresInARow} failures in a row; {left} not tried");
                        Announcer.AnnounceAfter(
                            // "so 1 photo wasn't tried", and nothing about the rest when none are left (#158, as the Mac's #159).
                            $"Background describing stopped: {Plural(FailuresInARow)} in a row couldn't be described{(left switch { 0 => "", 1 => ", so 1 photo wasn't tried", _ => $", so {Plural(left)} weren't tried" })}. " +
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
        if (_total > 0) Log.Write("background", $"Run ended: {_done} of {_total} done");
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
