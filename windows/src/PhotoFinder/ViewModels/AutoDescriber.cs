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

    /// <summary>Describing, or about to (the pause after arrowing): background describing waits for this.</summary>
    public bool IsBusy => _waiting || _running;

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
                    var text = await _describe(item, kind, _closing.Token);
                    if (text.Length > 0)
                    {
                        _store.Put(item, kind, text);
                        Speak(path, text);
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
