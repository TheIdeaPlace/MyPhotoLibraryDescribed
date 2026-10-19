using System.Globalization;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Tests;

/// <summary>Place Taken shows the place's name (#172), with a stand-in for Nominatim, so nothing is sent.</summary>
[Collection(SharedState.Name)]
public sealed class PlaceLookupsTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _asked = [];

    public PlaceLookupsTests()
    {
        PlaceNames.MinimumGap = TimeSpan.Zero;
        PlaceLookups.Pace = TimeSpan.Zero;
        PlaceLookups.FirstWait = TimeSpan.FromMilliseconds(10);
        PlaceLookups.LongestWait = TimeSpan.FromMilliseconds(40);
    }

    public void Dispose()
    {
        PlaceNames.MinimumGap = TimeSpan.FromSeconds(1);
        PlaceLookups.Pace = TimeSpan.FromSeconds(5);
        PlaceLookups.FirstWait = TimeSpan.FromMinutes(1);
        PlaceLookups.LongestWait = TimeSpan.FromMinutes(15);
        _lib.Dispose();
    }

    private static FileRow Row(string name, double latitude, double longitude) =>
        FileRow.ForItem(new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now)
            { Metadata = MediaMetadata.Empty with { Latitude = latitude, Longitude = longitude } });

    private static string LatOf(Uri uri) => System.Web.HttpUtility.ParseQueryString(uri.Query)["lat"]!;

    /// <summary>Nominatim's answer naming the spot by its latitude, so each spot has its own name.</summary>
    private static string Answer(Uri uri) => "{\"name\":\"Spot " + LatOf(uri) + "\",\"address\":{\"country\":\"Testland\"}}";

    private PlaceNames Service(Func<Uri, Task<string>>? answer = null, Func<CancellationToken, Task<string>>? getSwitch = null, string folder = "data") =>
        new(_lib.PathOf($@"{folder}\PlaceNames.json"), (uri, _) =>
        {
            lock (_asked) _asked.Add(LatOf(uri));
            return answer?.Invoke(uri) ?? Task.FromResult(Answer(uri));
        }, getSwitch);

    private static string Lat(double latitude) => latitude.ToString("0.00000", CultureInfo.InvariantCulture);

    [Fact]
    public async Task ARowOnScreen_GetsItsPlaceName_AndEveryPhotoShowingAtThatSpotToo()
    {
        var here = Row("a.jpg", 47.6, -122.3);
        var sameSpot = Row("b.jpg", 47.6, -122.3);
        var rows = new List<FileRow> { here, sameSpot };
        using var places = new PlaceLookups(Service(), () => true, () => rows, () => null);
        // The coordinates until the name is known.
        Assert.DoesNotContain("Spot", here.LocationText);
        places.Showing([here]);
        await places.WhenIdleAsync();
        Assert.Equal("Spot 47.60000, Testland", here.LocationText);
        Assert.Equal("Spot 47.60000, Testland", sameSpot.LocationText);
        Assert.Equal("Place taken Spot 47.60000, Testland", here.SpokenText("location"));
        // Asked once for the spot: b.jpg was named with it, and showing it asks nothing.
        places.Showing([sameSpot]);
        await places.WhenIdleAsync();
        Assert.Single(_asked);
    }

    [Fact]
    public async Task NothingIsLookedUp_UnlessWanted_AndTurningItOffShowsTheCoordinatesAgain()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var wanted = false;
        using var places = new PlaceLookups(Service(), () => wanted, () => [row], () => null);
        places.Showing([row]);
        places.Current(row);
        await places.WhenIdleAsync();
        Assert.Empty(_asked);
        wanted = true;
        places.Current(row);
        await places.WhenIdleAsync();
        Assert.StartsWith("Spot", row.LocationText);
        wanted = false;
        places.ShowKnown();
        Assert.DoesNotContain("Spot", row.LocationText);
        // Turned on again: the name known is shown at once, without asking.
        wanted = true;
        places.ShowKnown();
        Assert.StartsWith("Spot", row.LocationText);
        Assert.Single(_asked);
    }

    [Fact]
    public async Task WhatsOnScreenNow_ReplacesWhatWasWaiting_AndTheCurrentPhotoGoesFirst()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        var service = Service(async uri =>
        {
            if (first) { first = false; await gate.Task; }
            return Answer(uri);
        });
        // Half a degree apart, so all 100 are real latitudes.
        var rows = Enumerable.Range(0, 100).Select(i => Row($"{i}.jpg", 10 + i * 0.5, 20)).ToList();
        using var places = new PlaceLookups(service, () => true, () => rows, () => null);
        places.Showing(rows.Take(30));
        // Scrolled on while the first was being asked: those 30 aren't wanted any more (the first was already asked).
        places.Showing(rows.Skip(50).Take(60));
        places.Current(rows[99]);
        gate.SetResult();
        await places.WhenIdleAsync();
        Assert.Equal(Lat(10), _asked[0]);
        Assert.Equal(Lat(59.5), _asked[1]);
        // Then the 40 at the top of what's showing now: rows 50 to 89.
        Assert.Equal(Lat(35), _asked[2]);
        Assert.Equal(42, _asked.Count);
        Assert.Equal(Lat(54.5), _asked[^1]);
    }

    [Fact]
    public async Task RowsNoLongerShowing_ArentSent()
    {
        var a = Row("a.jpg", 1, 1);
        var b = Row("b.jpg", 2, 2);
        var c = Row("c.jpg", 3, 3);
        var rows = new List<FileRow> { a, b, c };
        var gate = new TaskCompletionSource();
        using var places = new PlaceLookups(Service(async uri => { if (LatOf(uri) == Lat(3)) await gate.Task; return Answer(uri); }),
            () => true, () => rows, () => null);
        places.Current(c);
        places.Showing([a, b]);
        // Another folder opened while c was being asked: a and b went.
        rows.Remove(a);
        rows.Remove(b);
        places.ShowKnown();
        gate.SetResult();
        await places.WhenIdleAsync();
        Assert.Equal([Lat(3)], _asked);
    }

    [Fact]
    public async Task APhotoPassed_IsForgotten_EvenWhenTheNextOneNeedsNothing()
    {
        var named = Row("named.jpg", 3, 3);
        var passed = Row("passed.jpg", 4, 4);
        var rows = new List<FileRow> { named, passed, Row("first.jpg", 5, 5) };
        // The spot of "named" is known already.
        Directory.CreateDirectory(_lib.PathOf("data"));
        File.WriteAllText(_lib.PathOf(@"data\PlaceNames.json"), "{\"3.00000,3.00000\":\"Known\"}");
        var slow = new TaskCompletionSource();
        var service = Service(async uri => { await slow.Task; return Answer(uri); });
        using var places = new PlaceLookups(service, () => true, () => rows, () => null);
        // Busy asking about the first; meanwhile you stop on "passed", then move on to one whose name is known.
        places.Current(rows[2]);
        places.Current(passed);
        places.Current(named);
        Assert.Equal("Known", named.LocationText);
        slow.SetResult();
        await places.WhenIdleAsync();
        Assert.DoesNotContain(Lat(4), _asked);
    }

    [Fact]
    public async Task AFailedPhoto_IsTriedAgain_AndAfterThreeFailuresLeftAlone()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var failures = 0;
        using var places = new PlaceLookups(Service(uri => ++failures <= 2 ? throw new System.Net.Http.HttpRequestException("Offline") : Task.FromResult(Answer(uri))),
            () => true, () => [row], () => null);
        places.Current(row);
        await places.WhenIdleAsync();
        // Tried again after each wait, and named the third time.
        Assert.Equal(3, _asked.Count);
        Assert.StartsWith("Spot", row.LocationText);

        var lost = Row("b.jpg", 1, 2);
        _asked.Clear();
        using var offline = new PlaceLookups(Service(_ => throw new System.Net.Http.HttpRequestException("Offline")), () => true, () => [lost], () => null);
        for (var i = 0; i < 3; i++)
        {
            offline.Current(lost);
            await offline.WhenIdleAsync();
        }
        Assert.Equal(PlaceLookups.MostTries, _asked.Count);
    }

    [Fact]
    public async Task PausedRemotely_OrRefused_ItStops_WithoutCountingItAgainstThePhoto()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var paused = Service(getSwitch: _ => Task.FromResult("""{"placeNames":{"windows":{"enabled":false}}}"""));
        using (var places = new PlaceLookups(paused, () => true, () => [row], () => null))
        {
            places.Current(row);
            await places.WhenIdleAsync();
        }
        Assert.Empty(_asked);

        var other = Row("b.jpg", 1, 1);
        // In a data folder of its own, so the first one's saved switch (off) doesn't apply.
        var refused = Service(_ => throw new System.Net.Http.HttpRequestException("Too many", null, System.Net.HttpStatusCode.TooManyRequests), folder: "other");
        using var stopped = new PlaceLookups(refused, () => true, () => [row, other], () => null);
        stopped.Showing([row, other]);
        await stopped.WhenIdleAsync();
        // One asked, refused: nothing more this session.
        Assert.Single(_asked);

        // A pause that ends: the photo is asked about then, as nothing was counted against it.
        _asked.Clear();
        var off = true;
        var switched = Service(getSwitch: _ => Task.FromResult(off ? """{"placeNames":{"windows":{"enabled":false}}}""" : """{"placeNames":{"windows":{"enabled":true}}}"""), folder: "third");
        PlaceNames.SwitchMaxAge = TimeSpan.Zero;
        try
        {
            using var resumed = new PlaceLookups(switched, () => true, () => [row], () => null);
            for (var i = 0; i < PlaceLookups.MostTries; i++)
            {
                resumed.Current(row);
                await resumed.WhenIdleAsync();
            }
            Assert.Empty(_asked);
            off = false;
            resumed.Current(row);
            await resumed.WhenIdleAsync();
            Assert.Single(_asked);
        }
        finally
        {
            PlaceNames.SwitchMaxAge = TimeSpan.FromDays(1);
        }
    }

    [Fact]
    public void TurningNamesOff_ClearsEveryRowShowing_EvenOnesNamedBeforeASearch()
    {
        var a = Row("a.jpg", 1, 1);
        var b = Row("b.jpg", 2, 2);
        a.SetPlaceName("Here");
        b.SetPlaceName("There");
        var showing = new List<FileRow> { a };
        var wanted = false;
        using var places = new PlaceLookups(Service(), () => wanted, () => showing, () => null);
        // Searched down to a, names turned off: a is cleared; then the search is cleared and b comes back.
        places.ShowKnown();
        showing.Add(b);
        places.ShowKnown();
        Assert.DoesNotContain("Here", a.LocationText);
        Assert.DoesNotContain("There", b.LocationText);
    }

    [Fact]
    public async Task AfterABurst_ItGoesGently()
    {
        PlaceLookups.Pace = TimeSpan.FromMilliseconds(60);
        var rows = Enumerable.Range(0, 13).Select(i => Row($"{i}.jpg", 10 + i, 20)).ToList();
        var times = new List<DateTime>();
        using var places = new PlaceLookups(Service(uri => { times.Add(DateTime.UtcNow); return Task.FromResult(Answer(uri)); }), () => true, () => rows, () => null);
        places.Showing(rows);
        await places.WhenIdleAsync();
        Assert.Equal(13, times.Count);
        // The first ten at once, then a pause before each of the rest.
        Assert.True(times[10] - times[9] >= TimeSpan.FromMilliseconds(45), $"{(times[10] - times[9]).TotalMilliseconds}");
    }

    [Fact]
    public async Task TheRowInFocus_IsNamedQuietly()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var heard = new List<string?>();
        row.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        using var places = new PlaceLookups(Service(), () => true, () => [row], () => row);
        places.Current(row);
        await places.WhenIdleAsync();
        Assert.Contains(nameof(FileRow.LocationText), heard);
        Assert.DoesNotContain(nameof(FileRow.AccessibleName), heard);
    }

    [Fact]
    public void ARowWhoseCoordinatesArriveLater_ShowsANameAlreadyKnown_OrSaysSoToBeLookedUp()
    {
        var arrived = new List<FileRow>();
        void Arrived(FileRow r) => arrived.Add(r);
        FileRow.CoordinatesArrived += Arrived;
        // An unusual spot, so rows other tests make at the same time aren't named by it.
        FileRow.KnownPlaceName = (lat, _) => lat == 12.34567 ? "Seattle" : null;
        try
        {
            var item = new MediaItem(@"C:\photos\a.jpg", 1000, DateTime.Now, DateTime.Now);
            var known = FileRow.ForItem(item);
            item.Metadata = MediaMetadata.Empty with { Latitude = 12.34567, Longitude = -122.3 };
            known.Refresh();
            Assert.Equal("Seattle", known.LocationText);
            Assert.DoesNotContain(known, arrived);

            var other = new MediaItem(@"C:\photos\b.jpg", 1000, DateTime.Now, DateTime.Now);
            var unknown = FileRow.ForItem(other);
            other.Metadata = MediaMetadata.Empty with { Latitude = 1, Longitude = 2 };
            unknown.Refresh();
            Assert.Contains(unknown, arrived);
            // A row made with its coordinates already doesn't say they arrived.
            var made = Row("c.jpg", 3, 4);
            Assert.DoesNotContain(made, arrived);
        }
        finally
        {
            FileRow.CoordinatesArrived -= Arrived;
            FileRow.KnownPlaceName = null;
        }
    }
}
