using System.Windows.Media;

namespace PhotoFinder.Services;

/// <summary>Small pictures for the Name column, loaded as rows scroll into view and kept for the most
/// recently seen rows. A few load at once, newest request first, so scrolling quickly doesn't queue up
/// thousands that are already off screen.</summary>
public sealed class ThumbnailCache
{
    private const int Capacity = 3000;
    private const int MaxConcurrent = 4;
    public const uint PixelSize = 48;

    private readonly Dictionary<string, LinkedListNode<(string Path, ImageSource? Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, ImageSource? Image)> _order = new();
    private readonly Stack<(string Path, Action<ImageSource?> Done, Func<bool>? Wanted)> _pending = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    /// <summary>Calls <paramref name="done"/> on the UI thread with the thumbnail (or null if there isn't one).
    /// A request whose <paramref name="stillWanted"/> says no by the time its turn comes (scrolled past) is dropped.</summary>
    public void Request(string path, Action<ImageSource?> done, Func<bool>? stillWanted = null)
    {
        if (_map.TryGetValue(path, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            done(node.Value.Image);
            return;
        }
        _pending.Push((path, done, stillWanted));
        Pump();
    }

    private void Pump()
    {
        while (_running < MaxConcurrent && _pending.Count > 0)
        {
            var (path, done, wanted) = _pending.Pop();
            if (_map.TryGetValue(path, out var cached)) { done(cached.Value.Image); continue; }
            if (wanted is not null && !wanted()) continue;
            if (!_inFlight.Add(path))
            {
                // Already loading: ask again when it's finished.
                _ = WaitThenAsync(path, done);
                continue;
            }
            _running++;
            _ = LoadAsync(path, done);
        }
    }

    private async Task WaitThenAsync(string path, Action<ImageSource?> done)
    {
        while (_inFlight.Contains(path)) await Task.Delay(50);
        if (_map.TryGetValue(path, out var node)) done(node.Value.Image);
    }

    private async Task LoadAsync(string path, Action<ImageSource?> done)
    {
        ImageSource? image = null;
        try
        {
            image = await ImageDecoding.ThumbnailAsync(path, PixelSize);
        }
        finally
        {
            _running--;
            _inFlight.Remove(path);
            var node = _order.AddFirst((path, image));
            _map[path] = node;
            while (_order.Count > Capacity)
            {
                _map.Remove(_order.Last!.Value.Path);
                _order.RemoveLast();
            }
            done(image);
            Pump();
        }
    }

    /// <summary>Forgets a file's thumbnail after it changes.</summary>
    public void Forget(string path)
    {
        if (_map.Remove(path, out var node)) _order.Remove(node);
    }
}
