using System.ComponentModel;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Describes the photo you arrow to, with Windows' on-device AI, and speaks the description as a UIA
/// notification, so it's heard without opening Get Info.
///
/// It waits for a short pause before starting, so arrowing through a list doesn't start a description for
/// every row. Only the photo you're on is described: one already being described is allowed to finish
/// (stopping the helper mid-description costs a model reload) and is saved, but it's spoken only if you're
/// still on it. Descriptions are saved, so going back to a photo speaks at once.
/// </summary>
public sealed class AutoDescriber : IDisposable
{
    public delegate Task<string> DescribeFunc(MediaItem item, string kind, CancellationToken cancel);

    private readonly LibraryModel _model;
    private readonly DescriptionStore _store;
    private readonly DescribeFunc _describe;
    private readonly Func<CancellationToken, Task<AIReadiness>> _readiness;
    private readonly TimeSpan _pause;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _wait;
    private MediaItem? _target;
    private bool _running;
    private bool _unavailable;
    /// <summary>The photo last moved to. The selection is re-applied whenever the list refreshes; that isn't
    /// moving, and mustn't speak the description again.</summary>
    private string? _lastPath;

    public AutoDescriber(LibraryModel model, DescriptionStore store, DescribeFunc describe,
        Func<CancellationToken, Task<AIReadiness>> readiness, TimeSpan? pause = null)
    {
        _model = model;
        _store = store;
        _describe = describe;
        _readiness = readiness;
        _pause = pause ?? TimeSpan.FromMilliseconds(600);
        _model.PropertyChanged += Model_PropertyChanged;
    }

    /// <summary>For tests: finishes when the current describing is done.</summary>
    internal Task Idle { get; private set; } = Task.CompletedTask;

    public bool Enabled
    {
        get => _model.Settings.AutoDescribe;
        set
        {
            _model.Settings.AutoDescribe = value;
            _model.SaveSettings();
            _unavailable = false;
            Announcer.Announce(value ? "Photos are described automatically" : "Automatic descriptions off");
            if (value) FocusChanged(force: true);
        }
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryModel.FocusedRow)) FocusChanged();
    }

    private void FocusChanged(bool force = false)
    {
        var path = _model.SelectedRows.Count == 1 ? _model.FocusedRow?.Item?.Path : null;
        if (!force && path == _lastPath) return;
        _lastPath = path;
        _wait?.Cancel();
        _target = null;
        if (!Enabled || _unavailable || _model.SelectedRows.Count != 1) return;
        // Videos can't be described; online-only photos would have to be downloaded first.
        if (_model.FocusedRow?.Item is not { IsVideo: false, IsOnlineOnly: false } item) return;

        var kind = _model.Settings.DescriptionKind;
        if (_store.Get(item, kind) is { } saved)
        {
            Show(item, saved);
            return;
        }
        _target = item;
        var wait = _wait = new CancellationTokenSource();
        Task.Delay(_pause, wait.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Idle = PumpAsync();
        }, CancellationToken.None, TaskContinuationOptions.None,
            SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task PumpAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (_target is { } item && !_closing.IsCancellationRequested)
            {
                var readiness = await _readiness(_closing.Token);
                if (!readiness.Usable)
                {
                    // Said once, then quiet until it's turned on again: not every arrow press.
                    _unavailable = true;
                    Announcer.Announce($"Photos can't be described automatically. {readiness.Message}");
                    return;
                }
                var kind = _model.Settings.DescriptionKind;
                try
                {
                    var text = await _describe(item, kind, _closing.Token);
                    _store.Put(item, kind, text);
                    if (_target == item) Show(item, text);
                }
                catch (DescriptionException ex)
                {
                    if (_target == item) Announcer.Announce(ex.Message);
                }
                if (_target == item) _target = null;
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
    private void Show(MediaItem item, string text)
    {
        if (_model.FocusedRow is not { } row || row.Item != item) return;
        row.Description = text;
        Announcer.Announce(text);
    }

    public void Dispose()
    {
        _model.PropertyChanged -= Model_PropertyChanged;
        _wait?.Cancel();
        _closing.Cancel();
    }
}
