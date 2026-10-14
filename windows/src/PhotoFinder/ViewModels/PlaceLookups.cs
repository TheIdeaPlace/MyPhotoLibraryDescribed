using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Place Taken shows the place's name when it's known (#172, as the Mac's #173): with Photo > Look Up Place Names on and
/// Place Taken a list column (in list view) or a gallery label (in the gallery), the rows showing are looked up, the
/// current photo first, a few at a time within Nominatim's one a second (<see cref="PlaceNames"/>). At most
/// <see cref="MostWaiting"/> rows wait, oldest dropped, so scrolling past a thousand photos doesn't queue them all. Names
/// already found are shown at once, without asking anyone. Rows never move when a name arrives; the list re-sorts only on
/// Sort, a load or Refresh.
///
/// When a lookup fails (offline, refused), the queue waits, from a minute up to a quarter of an hour, and a spot that fails
/// <see cref="MostTries"/> times isn't asked again until the next start.
/// </summary>
public sealed class PlaceLookups : IDisposable
{
    internal const int MostWaiting = 40;
    internal const int MostTries = 3;
    internal static TimeSpan FirstWait { get; set; } = TimeSpan.FromMinutes(1);
    internal static TimeSpan LongestWait { get; set; } = TimeSpan.FromMinutes(15);

    private readonly PlaceNames _names;
    private readonly Func<bool> _wanted;
    private readonly Func<IEnumerable<FileRow>> _rows;
    private readonly Func<FileRow?> _focused;
    private readonly LinkedList<FileRow> _waiting = new();
    private readonly HashSet<FileRow> _queued = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly CancellationTokenSource _closing = new();
    private FileRow? _current;
    private Task _running = Task.CompletedTask;
    private TimeSpan _wait = TimeSpan.Zero;

    /// <param name="wanted">Whether names are wanted now: the setting on and Place Taken showing.</param>
    /// <param name="rows">The rows in the library, for giving a name to every photo taken at that spot.</param>
    /// <param name="focused">The row in focus, whose spoken name is told quietly (it would be read again otherwise).</param>
    public PlaceLookups(PlaceNames names, Func<bool> wanted, Func<IEnumerable<FileRow>> rows, Func<FileRow?> focused)
    {
        _names = names;
        _wanted = wanted;
        _rows = rows;
        _focused = focused;
    }

    /// <summary>For tests: finishes once nothing is waiting or being looked up.</summary>
    internal Task WhenIdleAsync() => _running;

    /// <summary>Names already known, shown at once (or the coordinates, when names aren't wanted).</summary>
    public void ShowKnown()
    {
        var wanted = _wanted();
        var focused = _focused();
        foreach (var row in _rows())
        {
            if (row.Coordinates is not { } c) continue;
            row.SetPlaceName(wanted ? _names.TryCached(c.Latitude, c.Longitude) : null, quietly: row == focused);
        }
        if (!wanted)
        {
            _waiting.Clear();
            _queued.Clear();
            _current = null;
        }
    }

    /// <summary>A row came on screen in the list.</summary>
    public void Showing(FileRow row)
    {
        if (!Needs(row) || !_queued.Add(row)) return;
        _waiting.AddLast(row);
        while (_waiting.Count > MostWaiting)
        {
            _queued.Remove(_waiting.First!.Value);
            _waiting.RemoveFirst();
        }
        Run();
    }

    /// <summary>The photo you're on (in the gallery, once you've settled on it): looked up before any other.</summary>
    public void Current(FileRow row)
    {
        if (!Needs(row)) return;
        _current = row;
        Run();
    }

    /// <summary>A row that's wanted and has coordinates, not named yet, whose spot hasn't failed too often.</summary>
    private bool Needs(FileRow row)
    {
        if (!_wanted() || row.Coordinates is not { } c) return false;
        if (_names.TryCached(c.Latitude, c.Longitude) is { } known)
        {
            row.SetPlaceName(known, quietly: row == _focused());
            return false;
        }
        return _failures.GetValueOrDefault(PlaceNames.Key(c.Latitude, c.Longitude)) < MostTries;
    }

    private void Run()
    {
        if (_running.IsCompleted) _running = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_closing.IsCancellationRequested && _wanted() && !_names.Stopped)
        {
            FileRow row;
            if (_current is { } current)
            {
                row = current;
                _current = null;
            }
            else if (_waiting.First is { } first)
            {
                row = first.Value;
                _waiting.RemoveFirst();
                _queued.Remove(row);
            }
            else return;
            if (!Needs(row) || row.Coordinates is not { } c) continue;
            var (name, failed) = await _names.LookUpWithResultAsync(c.Latitude, c.Longitude, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            var key = PlaceNames.Key(c.Latitude, c.Longitude);
            if (failed)
            {
                _failures[key] = _failures.GetValueOrDefault(key) + 1;
                // Waits longer each time, up to a quarter of an hour, before asking again.
                _wait = _wait == TimeSpan.Zero ? FirstWait : TimeSpan.FromTicks(Math.Min(_wait.Ticks * 2, LongestWait.Ticks));
                Log.Write("places", $"Place Taken lookups wait {_wait.TotalSeconds:0} s after a failure");
                try { await Task.Delay(_wait, _closing.Token); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            _wait = TimeSpan.Zero;
            if (name is null) continue;
            // Every photo taken at that spot gets it.
            var focused = _focused();
            foreach (var other in _rows())
                if (other.Coordinates is { } o && PlaceNames.Key(o.Latitude, o.Longitude) == key)
                    other.SetPlaceName(name, quietly: other == focused);
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
