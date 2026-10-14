using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.ViewModels;

/// <summary>
/// Place Taken shows the place's name when it's known (#172, as the Mac's #173): with Photo > Look Up Place Names on and
/// Place Taken a list column (in list view) or a gallery label (in the gallery), the rows on screen are looked up, the
/// current photo first. Names already found are shown at once, without asking anyone. Rows never move when a name
/// arrives; the list re-sorts only on Sort, a load or Refresh.
///
/// Nominatim asks that all of an app's users together stay under one request a second, and discourages steady requests
/// from apps, so the queue goes gently, as the Mac does with Apple's service: <see cref="Burst"/> lookups, then one every
/// <see cref="Pace"/> (Get Info still asks at once). What's waiting is what's on screen now, replaced on every scan, at
/// most <see cref="MostWaiting"/>; rows that leave the screen, or a folder left, aren't sent.
///
/// When a lookup fails (offline), the queue waits, from a minute up to a quarter of an hour, and tries that photo again;
/// a spot that fails <see cref="MostTries"/> times isn't asked again until the next start. Paused remotely or refused by
/// Nominatim, it stops, without counting it against any photo.
/// </summary>
public sealed class PlaceLookups : IDisposable
{
    internal const int MostWaiting = 40;
    internal const int MostTries = 3;
    internal const int Burst = 10;
    internal static TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(5);
    internal static TimeSpan FirstWait { get; set; } = TimeSpan.FromMinutes(1);
    internal static TimeSpan LongestWait { get; set; } = TimeSpan.FromMinutes(15);

    private readonly PlaceNames _names;
    private readonly Func<bool> _wanted;
    private readonly Func<IReadOnlyList<FileRow>> _rows;
    private readonly Func<FileRow?> _focused;
    private readonly List<FileRow> _waiting = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly CancellationTokenSource _closing = new();
    private FileRow? _current;
    private Task _running = Task.CompletedTask;
    private TimeSpan _wait = TimeSpan.Zero;
    private int _asked;
    private DateTime _lastAsked = DateTime.MinValue;
    private bool _anyNamed;

    /// <param name="wanted">Whether names are wanted now: the setting on and Place Taken showing.</param>
    /// <param name="rows">The rows showing in the list, for giving a name to every photo taken at that spot, and so a row
    /// no longer among them isn't sent.</param>
    /// <param name="focused">The row in focus, whose spoken name is told quietly (it would be read again otherwise).</param>
    public PlaceLookups(PlaceNames names, Func<bool> wanted, Func<IReadOnlyList<FileRow>> rows, Func<FileRow?> focused)
    {
        _names = names;
        _wanted = wanted;
        _rows = rows;
        _focused = focused;
    }

    /// <summary>For tests: finishes once nothing is waiting or being looked up.</summary>
    internal Task WhenIdleAsync() => _running;

    /// <summary>After the rows change (another folder, a search) or names become wanted or not: names already known shown
    /// (or the coordinates again), and nothing waiting that was for rows no longer showing.</summary>
    public void ShowKnown()
    {
        var wanted = _wanted();
        _waiting.Clear();
        var focused = _focused();
        if (_current is { } current && current != focused) _current = null;
        if (!wanted) _current = null;
        if (!wanted && !_anyNamed) return;
        _anyNamed = false;
        foreach (var row in _rows())
        {
            if (row.Coordinates is not { } c) continue;
            var name = wanted ? _names.TryCached(c.Latitude, c.Longitude) : null;
            row.SetPlaceName(name, quietly: row == focused);
            _anyNamed |= name is not null;
        }
    }

    /// <summary>The list's rows on screen now, top to bottom: they replace whatever was waiting.</summary>
    public void Showing(IEnumerable<FileRow> rows)
    {
        _waiting.Clear();
        foreach (var row in rows)
        {
            if (_waiting.Count >= MostWaiting) break;
            if (Needs(row) && row != _current) _waiting.Add(row);
        }
        Run();
    }

    /// <summary>The photo you're on (in the gallery, once you've settled on it): looked up before any other. Moving on
    /// forgets the one before, so a photo passed isn't sent.</summary>
    public void Current(FileRow row)
    {
        _current = null;
        if (!Needs(row)) return;
        _current = row;
        Run();
    }

    /// <summary>A row that's wanted and has coordinates, not named yet, whose spot hasn't failed too often. One whose name
    /// is known is named on the way.</summary>
    private bool Needs(FileRow row)
    {
        if (!_wanted() || row.Coordinates is not { } c) return false;
        if (_names.TryCached(c.Latitude, c.Longitude) is { } known)
        {
            row.SetPlaceName(known, quietly: row == _focused());
            _anyNamed = true;
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
        try
        {
            while (!_closing.IsCancellationRequested && _wanted())
            {
                FileRow row;
                if (_current is { } current) row = current;
                else if (_waiting.Count > 0) row = _waiting[0];
                else return;
                // Still showing, still needing a name.
                if (!_rows().Contains(row) || !Needs(row) || row.Coordinates is not { } c)
                {
                    Forget(row);
                    continue;
                }
                // Gently: a burst, then one every few seconds; after a quiet minute, a burst again.
                if (DateTime.UtcNow - _lastAsked > TimeSpan.FromMinutes(1)) _asked = 0;
                if (_asked >= Burst && Pace - (DateTime.UtcNow - _lastAsked) is { Ticks: > 0 } gap)
                {
                    try { await Task.Delay(gap, _closing.Token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                var (name, outcome) = await _names.LookUpWithResultAsync(c.Latitude, c.Longitude, _closing.Token);
                if (_closing.IsCancellationRequested) return;
                if (outcome is PlaceNames.Outcome.Answered or PlaceNames.Outcome.Failed)
                {
                    _asked++;
                    _lastAsked = DateTime.UtcNow;
                }
                var key = PlaceNames.Key(c.Latitude, c.Longitude);
                switch (outcome)
                {
                    case PlaceNames.Outcome.Stopped:
                        // Paused remotely or refused: nothing more until asked again (a pause is checked then).
                        _waiting.Clear();
                        _current = null;
                        return;
                    case PlaceNames.Outcome.Failed:
                        _failures[key] = _failures.GetValueOrDefault(key) + 1;
                        // The photo stays where it is in line, tried again after a wait, longer each time.
                        _wait = _wait == TimeSpan.Zero ? FirstWait : TimeSpan.FromTicks(Math.Min(_wait.Ticks * 2, LongestWait.Ticks));
                        Log.Write("places", $"Place Taken lookups wait {_wait.TotalSeconds:0} s after a failure");
                        try { await Task.Delay(_wait, _closing.Token); }
                        catch (OperationCanceledException) { return; }
                        continue;
                }
                _wait = TimeSpan.Zero;
                Forget(row);
                if (name is null) continue;
                // Every photo showing that was taken at that spot gets it.
                var focused = _focused();
                foreach (var other in _rows())
                    if (other.Coordinates is { } o && PlaceNames.Key(o.Latitude, o.Longitude) == key)
                        other.SetPlaceName(name, quietly: other == focused);
                _anyNamed = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("places", "Place Taken lookups stopped", ex);
        }
    }

    private void Forget(FileRow row)
    {
        if (_current == row) _current = null;
        _waiting.Remove(row);
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
