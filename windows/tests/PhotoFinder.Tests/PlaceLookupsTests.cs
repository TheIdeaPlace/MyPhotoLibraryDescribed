using System.ComponentModel;
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
        PlaceLookups.FirstWait = TimeSpan.FromMilliseconds(10);
        PlaceLookups.LongestWait = TimeSpan.FromMilliseconds(40);
    }

    public void Dispose()
    {
        PlaceNames.MinimumGap = TimeSpan.FromSeconds(1);
        PlaceLookups.FirstWait = TimeSpan.FromMinutes(1);
        PlaceLookups.LongestWait = TimeSpan.FromMinutes(15);
        _lib.Dispose();
    }

    private static FileRow Row(string name, double latitude, double longitude) =>
        FileRow.ForItem(new MediaItem($@"C:\photos\{name}", 1000, DateTime.Now, DateTime.Now)
            { Metadata = MediaMetadata.Empty with { Latitude = latitude, Longitude = longitude } });

    /// <summary>Nominatim's answer naming the spot by its latitude, so each spot has its own name.</summary>
    private static string Answer(Uri uri)
    {
        var lat = System.Web.HttpUtility.ParseQueryString(uri.Query)["lat"];
        return "{\"name\":\"Spot " + lat + "\",\"address\":{\"country\":\"Testland\"}}";
    }

    private PlaceNames Service(Func<Uri, Task<string>>? answer = null) => new(_lib.PathOf(@"data\PlaceNames.json"), (uri, _) =>
    {
        lock (_asked) _asked.Add(System.Web.HttpUtility.ParseQueryString(uri.Query)["lat"]!);
        return answer?.Invoke(uri) ?? Task.FromResult(Answer(uri));
    });

    private static string Lat(double latitude) => latitude.ToString("0.00000", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AShowingRow_GetsItsPlaceName_AndEveryPhotoAtThatSpotToo()
    {
        var here = Row("a.jpg", 47.6, -122.3);
        var sameSpot = Row("b.jpg", 47.6, -122.3);
        var rows = new List<FileRow> { here, sameSpot };
        using var places = new PlaceLookups(Service(), () => true, () => rows, () => null);
        // The coordinates until the name is known.
        Assert.DoesNotContain("Spot", here.LocationText);
        places.Showing(here);
        await places.WhenIdleAsync();
        Assert.Equal("Spot 47.60000, Testland", here.LocationText);
        Assert.Equal("Spot 47.60000, Testland", sameSpot.LocationText);
        Assert.Equal("Place taken Spot 47.60000, Testland", here.SpokenText("location"));
        // Asked once for the spot: b.jpg was named with it, and showing it asks nothing.
        places.Showing(sameSpot);
        await places.WhenIdleAsync();
        Assert.Single(_asked);
    }

    [Fact]
    public async Task NothingIsLookedUp_UnlessWanted_AndTurningItOffShowsTheCoordinatesAgain()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var wanted = false;
        using var places = new PlaceLookups(Service(), () => wanted, () => [row], () => null);
        places.Showing(row);
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
    public async Task TheCurrentPhoto_IsLookedUpFirst_AndAtMost40Wait()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        var service = Service(async uri =>
        {
            if (first) { first = false; await gate.Task; }
            return Answer(uri);
        });
        var rows = Enumerable.Range(0, 50).Select(i => Row($"{i}.jpg", 10 + i, 20)).ToList();
        using var places = new PlaceLookups(service, () => true, () => rows, () => null);
        places.Showing(rows[0]);
        for (var i = 1; i < 46; i++) places.Showing(rows[i]);
        places.Current(rows[49]);
        gate.SetResult();
        await places.WhenIdleAsync();
        // The first (already asked), then the current photo, then only the 40 newest that waited.
        Assert.Equal(Lat(10), _asked[0]);
        Assert.Equal(Lat(59), _asked[1]);
        Assert.Equal(42, _asked.Count);
        Assert.Equal(Lat(16), _asked[2]);
        Assert.Equal(Lat(55), _asked[^1]);
    }

    [Fact]
    public async Task ASpotThatFailsThreeTimes_IsntAskedAgain_ThisSession()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        using var places = new PlaceLookups(Service(_ => throw new System.Net.Http.HttpRequestException("Offline")), () => true, () => [row], () => null);
        for (var i = 0; i < 5; i++)
        {
            places.Current(row);
            await places.WhenIdleAsync();
        }
        Assert.Equal(PlaceLookups.MostTries, _asked.Count);
        Assert.DoesNotContain("Spot", row.LocationText);
    }

    [Fact]
    public async Task TheRowInFocus_IsNamedQuietly_AndToldWhenFocusLeaves()
    {
        var row = Row("a.jpg", 47.6, -122.3);
        var heard = new List<string?>();
        row.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        using var places = new PlaceLookups(Service(), () => true, () => [row], () => row);
        places.Current(row);
        await places.WhenIdleAsync();
        Assert.Contains(nameof(FileRow.LocationText), heard);
        Assert.DoesNotContain(nameof(FileRow.AccessibleName), heard);
        row.NotifyAccessibleName();
        Assert.Contains(nameof(FileRow.AccessibleName), heard);
    }
}
