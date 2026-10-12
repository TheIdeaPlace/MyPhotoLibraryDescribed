using System.ComponentModel;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Describes the photo you arrow to, with Windows' on-device AI, and speaks the description as a UIA
/// notification, so it's heard without opening Get Info.
///
/// - It waits for a short pause before speaking or describing, saved descriptions included, so the row's
///   own name is heard first and arrowing through a list doesn't start a description for every row.
/// - Descriptions are queued behind other speech rather than cutting it off, under their own activity, so
///   moving on silences them the way any focus change does.
/// - Only the photo you're on is described. One already being described finishes (stopping the helper
///   costs a model reload) and is saved, but it's spoken only if you're still on that photo, in the list or
///   gallery, with the main window in front.
/// - It never starts the model download by itself, and says nothing when Windows AI isn't available unless
///   you turned the feature on yourself.
/// </summary>
public sealed class AutoDescriber : IDisposable
{
    public delegate Task<string> DescribeFunc(MediaItem item, string kind, CancellationToken cancel);

    private readonly LibraryModel _model;
    private readonly DescriptionStore _store;
    private readonly DescribeFunc _describe;
    private readonly Func<CancellationToken, Task<AIReadiness>> _check;
    private readonly Func<CancellationToken, Task<AIReadiness>> _prepare;
    private readonly TimeSpan _pause;
    private readonly TaskScheduler _ui;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _wait;
    private string? _target;
    private bool _running;
    private bool _waiting;
    private bool _unavailable;
    /// <summary>Describe Photo requests in progress.</summary>
    private int _manual;
    /// <summary>The photos Describe Photo or Describe Again is working on, so asking again doesn't describe one twice.</summary>
    private readonly Dictionary<string, Task> _manualRuns = [];
    /// <summary>The photo automatic describing is asking Windows about right now.</summary>
    private string? _describingPath;
    /// <summary>Photos Describe Again is describing, with how many requests: automatic describing leaves them alone
    /// meanwhile, and an automatic description of one that was already under way is older, so it's neither saved nor
    /// spoken. Counted, as on the Mac, so one request finishing doesn't unprotect another.</summary>
    private readonly Dictionary<string, int> _againPaths = [];
    /// <summary>The photo last moved to. The selection is re-applied whenever the list refreshes; that isn't
    /// moving, and mustn't speak the description again.</summary>
    private string? _lastPath;
    /// <summary>The photo whose description was last spoken.</summary>
    private string? _spokenPath;

    /// <param name="check">Whether Windows AI can describe now. Never downloads anything.</param>
    /// <param name="prepare">Gets the model ready, downloading it if needed. Only used when the person turns the feature on.</param>
    public AutoDescriber(LibraryModel model, DescriptionStore store, DescribeFunc describe,
        Func<CancellationToken, Task<AIReadiness>> check, Func<CancellationToken, Task<AIReadiness>> prepare, TimeSpan? pause = null)
    {
        _model = model;
        _store = store;
        _describe = describe;
        _check = check;
        _prepare = prepare;
        _pause = pause ?? TimeSpan.FromMilliseconds(700);
        // Created on the UI thread, so everything it says and changes happens there too.
        _ui = TaskScheduler.FromCurrentSynchronizationContext();
        _model.PropertyChanged += Model_PropertyChanged;
    }

    /// <summary>Whether the person is on the photo: the list or gallery has focus and the main window is in front.
    /// Set by the window; descriptions aren't made or spoken while it's false.</summary>
    public Func<bool> CanSpeak { get; set; } = () => true;

    /// <summary>Whether a photo is being described right now, automatically or because you asked: the Description panel
    /// says "Describing…".</summary>
    public bool IsDescribing(string path) =>
        string.Equals(_describingPath, path, StringComparison.OrdinalIgnoreCase)
        || (_manualRuns.TryGetValue(path, out var run) && !run.IsCompleted);

    /// <summary>A photo started or stopped being described.</summary>
    public event Action? DescribingChanged;

    /// <summary>Describing, or about to (the pause after arrowing): background describing waits for this.</summary>
    public bool IsBusy => _waiting || _running || _manual > 0;

    public bool Enabled
    {
        get => _model.Settings.AutoDescribe;
        set
        {
            _model.Settings.AutoDescribe = value;
            _model.SaveSettings();
            Reset();
            if (!value)
            {
                Announcer.Announce("Automatic descriptions off");
                return;
            }
            Announcer.Announce("Photos are described automatically");
            Busy(TurnedOnAsync());
        }
    }

    /// <summary>Turned on by the person: now's the time to say why it can't work, or to get the model ready.</summary>
    private async Task TurnedOnAsync()
    {
        _unavailable = false;
        var readiness = await _check(_closing.Token);
        if (readiness.State == "NotReady")
        {
            Announcer.Announce("Getting Windows' image description model ready. The first time, this can take a few minutes.");
            readiness = await _prepare(_closing.Token);
        }
        if (readiness.State != "Ready")
        {
            _unavailable = true;
            Announcer.Announce($"Photos can't be described automatically. {readiness.Message}");
            return;
        }
        FocusChanged(force: true);
    }

    /// <summary>The list or gallery got keyboard focus back: describe the photo there if it hasn't been spoken yet
    /// (arrowing in the sidebar selects photos without moving to them).</summary>
    public void Refocused()
    {
        if (CurrentPath() is { } path && path != _spokenPath) FocusChanged(force: true);
    }

    /// <summary>
    /// Photo > Describe Photo (Ctrl+Shift+D): describes <paramref name="row"/> now and speaks it, whether or not
    /// automatic descriptions are on. Since you asked, it gets Windows' model ready if that's all it needs, and says
    /// why when it can't. <paramref name="stillOn"/> says whether you're still on that photo when it's done.
    /// With <paramref name="again"/> (Photo > Describe Again, Ctrl+Alt+Shift+D), a saved description isn't used: the
    /// photo is described afresh and the new description replaces it, for one that's poor or from another prompt.
    /// </summary>
    /// <param name="replaceEdited">Describe Again on a description the person corrected, after they agreed to replace it.
    /// Otherwise a correction made meanwhile is kept and the new description dropped.</param>
    public Task DescribeNowAsync(FileRow row, Func<bool> stillOn, bool again = false, bool replaceEdited = false)
    {
        if (row.Item is { } item && _manualRuns.TryGetValue(item.Path, out var running) && !running.IsCompleted)
        {
            Announcer.Announce($"Still describing {item.Name}");
            return running;
        }
        var task = DescribeNowCoreAsync(row, stillOn, again, replaceEdited);
        if (row.Item is { } described && !task.IsCompleted)
        {
            _manualRuns[described.Path] = task;
            DescribingChanged?.Invoke();
            var path = described.Path;
            task.ContinueWith(_ =>
            {
                if (_manualRuns.TryGetValue(path, out var t) && t == task) _manualRuns.Remove(path);
                DescribingChanged?.Invoke();
            }, CancellationToken.None, TaskContinuationOptions.None, _ui);
        }
        Busy(task);
        return task;
    }

    private async Task DescribeNowCoreAsync(FileRow row, Func<bool> stillOn, bool again, bool replaceEdited)
    {
        if (row.Item is not { } item)
        {
            Announcer.Announce("Only photos can be described.");
            return;
        }
        if (item.IsVideo)
        {
            Announcer.Announce("Videos can't be described.");
            return;
        }
        if (item.IsOnlineOnly)
        {
            Announcer.Announce("This photo is only online. Open it once to download it, then describe it.");
            return;
        }
        // Automatic describing of the same photo would only say it again.
        if (_target == item.Path)
        {
            _wait?.Cancel();
            _waiting = false;
            _target = null;
        }
        var kind = _model.Settings.DescriptionKind;
        if (!again && _store.Get(item, kind) is { } saved)
        {
            row.Description = saved;
            Announcer.Announce(saved);
            _spokenPath = item.Path;
            return;
        }
        _manual++;
        if (again) _againPaths[item.Path] = _againPaths.GetValueOrDefault(item.Path) + 1;
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
                Announcer.Announce($"This photo can't be described. {readiness.Message}");
                return;
            }
            _unavailable = false;
            Announcer.Announce(again ? $"Describing {item.Name} again" : $"Describing {item.Name}");
            // Automatic describing is already asking about this photo: wait for that rather than asking twice. Describe
            // Again still asks afresh afterwards; the automatic result isn't saved (see _againPaths).
            while (_describingPath == item.Path) await Task.Delay(50, _closing.Token);
            var text = (again ? null : _store.Get(item, kind)) ?? await _describe(item, kind, _closing.Token);
            if (text.Length == 0)
            {
                if (stillOn()) Announcer.Announce("Windows returned an empty description.");
                return;
            }
            if (replaceEdited) _store.Put(item, kind, text);
            else if (!_store.PutUnlessEdited(item, kind, text)) return;  // corrected in the Description panel meanwhile: kept
            row.Description = text;
            if (!stillOn()) return;
            Announcer.Announce(text);
            _spokenPath = item.Path;
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            if (stillOn()) Announcer.Announce(ex is DescriptionException ? ex.Message : $"Windows couldn't describe this photo: {ex.Message}");
        }
        finally
        {
            _manual--;
            if (again && --_againPaths[item.Path] == 0) _againPaths.Remove(item.Path);
        }
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryModel.FocusedRow)) FocusChanged();
    }

    private string? CurrentPath() => _model.SelectedRows.Count == 1 ? _model.FocusedRow?.Item?.Path : null;

    private void FocusChanged(bool force = false)
    {
        var path = CurrentPath();
        if (!force && path == _lastPath)
        {
            // The same photo after the list refreshed: new rows start without the description, so put it back quietly.
            if (_model.FocusedRow is { Item: { } same } row && row.Description.Length == 0
                && _store.Get(same, _model.Settings.DescriptionKind) is { } saved)
                row.Description = saved;
            return;
        }
        _lastPath = path;
        _wait?.Cancel();
        _waiting = false;
        _target = null;
        if (!Enabled || _unavailable || path is null) return;
        // Videos can't be described; online-only photos would have to be downloaded first.
        if (_model.FocusedRow?.Item is not { IsVideo: false, IsOnlineOnly: false }) return;

        _target = path;
        _waiting = true;
        var wait = _wait = new CancellationTokenSource();
        Task.Delay(_pause, wait.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _waiting = false;
            Busy(PumpAsync());
        }, CancellationToken.None, TaskContinuationOptions.None, _ui);
    }

    private async Task PumpAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (_target is { } path && !_closing.IsCancellationRequested)
            {
                if (_model.FocusedRow is not { Item: { } item } || item.Path != path || !CanSpeak())
                {
                    // Moved on, or not on the photo (in the sidebar, a dialog): don't spend the NPU on it.
                    _target = null;
                    break;
                }
                if (_againPaths.ContainsKey(path))
                {
                    // Describe Again is describing it: it speaks the new description itself. Saying the old saved one,
                    // or asking the model twice, would only get in the way.
                    _target = null;
                    break;
                }
                var kind = _model.Settings.DescriptionKind;
                if (_store.Get(item, kind) is { } saved)
                {
                    Speak(path, saved);
                    if (_target == path) _target = null;
                    continue;
                }
                var readiness = await _check(_closing.Token);
                if (readiness.State != "Ready")
                {
                    // Not on this PC, turned off in Windows, or the model isn't downloaded: stay quiet. Turning the
                    // feature on with Ctrl+Shift+U says why, and gets the model ready if that's all it needs.
                    _unavailable = true;
                    _target = null;
                    break;
                }
                try
                {
                    _describingPath = path;
                    DescribingChanged?.Invoke();
                    string text;
                    try
                    {
                        text = await _describe(item, kind, _closing.Token);
                    }
                    finally
                    {
                        _describingPath = null;
                        DescribingChanged?.Invoke();
                    }
                    if (_againPaths.ContainsKey(path))
                    {
                        // Describe Again started on this photo meanwhile: its description is the newer one.
                    }
                    else if (text.Length > 0)
                    {
                        // A correction made in the Description panel while it was being described is kept.
                        if (_store.PutUnlessEdited(item, kind, text)) Speak(path, text);
                    }
                    else if (_target == path && CanSpeak())
                    {
                        Announcer.Announce("Windows returned an empty description.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A content filter, an unreadable file, the helper not starting: said for the photo you're on.
                    if (_target == path && CanSpeak())
                        Announcer.Announce(ex is DescriptionException ? ex.Message : $"Windows couldn't describe this photo: {ex.Message}");
                }
                if (_target == path) _target = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Puts the description on the row (the gallery shows it) and speaks it, if that photo is still the one you're on.</summary>
    private void Speak(string path, string text)
    {
        if (_model.FocusedRow is not { Item: { } item } row || item.Path != path) return;
        row.Description = text;
        if (_target != path || !CanSpeak()) return;
        Announcer.AnnounceAfter(text, "PhotoFinderDescription");
        _spokenPath = path;
    }

    private void Reset()
    {
        _wait?.Cancel();
        _waiting = false;
        _target = null;
        _lastPath = null;
        _spokenPath = null;
    }

    private readonly List<Task> _busy = [];

    private void Busy(Task task)
    {
        _busy.RemoveAll(t => t.IsCompleted);
        _busy.Add(task);
    }

    /// <summary>For tests: finishes once nothing is waiting or being described.</summary>
    internal async Task WhenIdleAsync(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while ((_waiting || _running || _busy.Any(t => !t.IsCompleted)) && DateTime.UtcNow < until)
            await Task.Delay(10);
    }

    public void Dispose()
    {
        _model.PropertyChanged -= Model_PropertyChanged;
        _wait?.Cancel();
        _closing.Cancel();
    }
}
