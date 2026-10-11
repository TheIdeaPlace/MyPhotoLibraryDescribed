using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Describes the photos in a folder behind the scenes when the folder is added to the library (a setting, off
/// until turned on), so their descriptions are ready, and spoken at once, when you arrow to them.
///
/// One photo at a time, saving each to the same store automatic descriptions and Get Info use. Photos already
/// described, videos, and online-only cloud photos (describing would download them) are skipped. Progress is in
/// the status bar, and finishing is announced. It stops when the app closes; the photos it hadn't reached are
/// described when you arrow to them, as usual.
/// </summary>
public sealed class BackgroundDescriber : IDisposable
{
    private readonly LibraryModel _model;
    private readonly DescriptionStore _store;
    private readonly AutoDescriber.DescribeFunc _describe;
    private readonly Func<CancellationToken, Task<AIReadiness>> _check;
    private readonly Func<CancellationToken, Task<AIReadiness>> _prepare;
    private readonly Queue<(MediaItem Item, string Folder)> _queue = new();
    private readonly CancellationTokenSource _closing = new();
    private Task _running = Task.CompletedTask;
    private int _total;
    private int _done;
    private readonly Dictionary<string, int> _perFolder = new(StringComparer.OrdinalIgnoreCase);

    public BackgroundDescriber(LibraryModel model, DescriptionStore store, AutoDescriber.DescribeFunc describe,
        Func<CancellationToken, Task<AIReadiness>> check, Func<CancellationToken, Task<AIReadiness>> prepare)
    {
        _model = model;
        _store = store;
        _describe = describe;
        _check = check;
        _prepare = prepare;
        _model.FolderAdded += FolderAdded;
    }

    /// <summary>For tests: finishes when the queue is empty.</summary>
    internal Task Idle => _running;

    public bool Enabled
    {
        get => _model.Settings.DescribeInBackground;
        set
        {
            _model.Settings.DescribeInBackground = value;
            _model.SaveSettings();
            if (!value)
            {
                _queue.Clear();
                Announcer.Announce("Added folders won't be described in the background");
                return;
            }
            Announcer.Announce("Folders you add will be described in the background");
            _ = CheckWhenTurnedOnAsync();
        }
    }

    /// <summary>Turned on by the person: say now if it can't work, or get the model ready if that's all it needs.</summary>
    private async Task CheckWhenTurnedOnAsync()
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
                Announcer.Announce($"Photos can't be described in the background. {readiness.Message}");
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    private void FolderAdded(string folder, IReadOnlyList<MediaItem> items)
    {
        if (!Enabled) return;
        var kind = _model.Settings.DescriptionKind;
        var name = Path.GetFileName(folder);
        var todo = items.Where(i => !i.IsVideo && !i.IsOnlineOnly && _store.Get(i, kind) is null).ToList();
        if (todo.Count == 0) return;
        foreach (var item in todo) _queue.Enqueue((item, name));
        _perFolder[name] = _perFolder.GetValueOrDefault(name) + todo.Count;
        _total += todo.Count;
        Announcer.AnnounceAfter($"Describing {Plural(todo.Count)} from {name} in the background", "PhotoFinderBackground");
        ShowProgress();
        if (_running.IsCompleted) _running = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var readiness = await _check(_closing.Token);
            if (readiness.State != "Ready")
            {
                // Turning the setting on already said why; adding a folder shouldn't keep saying it.
                _queue.Clear();
                Finish();
                return;
            }
            while (_queue.Count > 0 && !_closing.IsCancellationRequested)
            {
                var (item, folder) = _queue.Dequeue();
                var kind = _model.Settings.DescriptionKind;
                if (_store.Get(item, kind) is null)
                {
                    try
                    {
                        var text = await _describe(item, kind, _closing.Token);
                        if (text.Length > 0) _store.Put(item, kind, text);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // One photo that can't be described (filtered, unreadable) doesn't stop the rest.
                    }
                }
                _done++;
                ShowProgress();
                if (_perFolder.ContainsKey(folder) && --_perFolder[folder] == 0)
                {
                    _perFolder.Remove(folder);
                    Announcer.AnnounceAfter($"Finished describing the photos in {folder}", "PhotoFinderBackground");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Finish();
    }

    private void ShowProgress() =>
        _model.BackgroundProgress = $"Describing photos in the background, {Math.Min(_done + 1, _total):N0} of {_total:N0}";

    private void Finish()
    {
        _total = 0;
        _done = 0;
        _perFolder.Clear();
        _model.BackgroundProgress = null;
    }

    private static string Plural(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";

    public void Dispose()
    {
        _model.FolderAdded -= FolderAdded;
        _closing.Cancel();
    }
}
