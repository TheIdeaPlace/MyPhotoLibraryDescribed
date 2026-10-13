using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Photo > Look Up Place Names (#25), with a stand-in for OpenStreetMap's Nominatim, so nothing is sent.</summary>
[Collection(SharedState.Name)]
public sealed class PlaceNamesTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<Uri> _asked = [];
    private readonly List<string> _announced = [];

    /// <summary>Nominatim's answer for 47.60970, -122.34220 (Seattle), 13 Oct 2026.</summary>
    private const string Seattle = """
        {"place_id":324408756,"licence":"Data © OpenStreetMap contributors, ODbL 1.0. http://osm.org/copyright","osm_type":"node","osm_id":4039470782,"lat":"47.6097779","lon":"-122.3421032","category":"amenity","type":"bicycle_parking","place_rank":30,"importance":7.72983901731444e-05,"addresstype":"amenity","name":"","display_name":"85, Pike Street, West Edge, Belltown, Seattle, King County, Washington, 98101, United States","address":{"house_number":"85","road":"Pike Street","neighbourhood":"West Edge","suburb":"Belltown","city":"Seattle","county":"King County","state":"Washington","ISO3166-2-lvl4":"US-WA","postcode":"98101","country":"United States","country_code":"us"},"boundingbox":["47.6097279","47.6098279","-122.3421532","-122.3420532"]}
        """;

    public PlaceNamesTests()
    {
        PlaceNames.MinimumGap = TimeSpan.FromMilliseconds(200);
        Announcer.Observer = _announced.Add;
    }

    public void Dispose()
    {
        PlaceNames.MinimumGap = TimeSpan.FromSeconds(1);
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private PlaceNames Service(string answer = Seattle) => new(_lib.PathOf(@"data\PlaceNames.json"), (uri, _) =>
    {
        _asked.Add(uri);
        return Task.FromResult(answer);
    });

    [Fact]
    public void TheName_IsThePlaceTownStateAndCountry_WithoutRepeats()
    {
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", PlaceNames.NameFrom(Seattle));
        Assert.Equal("Pike Place Market, Seattle, Washington, United States",
            PlaceNames.NameFrom("""{"name":"Pike Place Market","address":{"road":"Pike Place","city":"Seattle","state":"Washington","country":"United States"}}"""));
        // A city-state isn't said twice.
        Assert.Equal("Marina Bay Sands, Singapore",
            PlaceNames.NameFrom("""{"name":"Marina Bay Sands","address":{"city":"Singapore","country":"Singapore"}}"""));
        Assert.Equal("Tórshavn, Faroe Islands", PlaceNames.NameFrom("""{"name":"","address":{"town":"Tórshavn","country":"Faroe Islands"}}"""));
        Assert.Null(PlaceNames.NameFrom("""{"error":"Unable to geocode"}"""));
        Assert.Null(PlaceNames.NameFrom("not json"));
    }

    [Fact]
    public async Task ARequest_IdentifiesItself_SendsOnlyTheCoordinates_AndIsMadeOncePerPlace()
    {
        var places = Service();
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await places.LookUpAsync(47.609701, -122.342199));
        var uri = Assert.Single(_asked);
        Assert.Equal("nominatim.openstreetmap.org", uri.Host);
        Assert.Contains("lat=47.60970", uri.Query);
        Assert.Contains("lon=-122.34220", uri.Query);
        Assert.Contains("format=jsonv2", uri.Query);

        // The same spot again, here or after a restart: from the file, not sent.
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await places.LookUpAsync(47.609700, -122.342200));
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await Service().LookUpAsync(47.6097, -122.3422));
        Assert.Single(_asked);
    }

    [Fact]
    public async Task RequestsAreSpacedOut_AsNominatimAsks()
    {
        var places = Service();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(places.LookUpAsync(1, 1), places.LookUpAsync(2, 2), places.LookUpAsync(3, 3));
        Assert.Equal(3, _asked.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(380), $"Took {watch.Elapsed}");
    }

    [Fact]
    public async Task AnAnswerThatIsntNominatims_IsntRemembered_ButANoNameAnswerIs()
    {
        var answers = new Queue<string>(["<html>Sign in to the hotel Wi-Fi</html>", Seattle]);
        var calls = 0;
        var places = new PlaceNames(_lib.PathOf(@"data\PlaceNames.json"), (_, _) => { calls++; return Task.FromResult(answers.Dequeue()); });
        Assert.Null(await places.LookUpAsync(47.6097, -122.3422));
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await places.LookUpAsync(47.6097, -122.3422));
        Assert.Equal(2, calls);

        // The open sea: no name, remembered, not asked again.
        var sea = new PlaceNames(_lib.PathOf(@"data\PlaceNames.json"), (_, _) => { calls++; return Task.FromResult("""{"error":"Unable to geocode"}"""); });
        Assert.Null(await sea.LookUpAsync(0, -30));
        Assert.Null(await sea.LookUpAsync(0, -30));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Refused_StopsAsking_ForTheSession()
    {
        var calls = 0;
        var places = new PlaceNames(_lib.PathOf(@"data\PlaceNames.json"), (_, _) =>
        {
            calls++;
            throw new System.Net.Http.HttpRequestException("Too many", null, System.Net.HttpStatusCode.TooManyRequests);
        });
        Assert.Null(await places.LookUpAsync(1, 1));
        Assert.Null(await places.LookUpAsync(2, 2));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClosingGetInfoMidRequest_GivesUp_AndTheNextLookupStillRuns()
    {
        var release = new TaskCompletionSource<string>();
        var first = true;
        var places = new PlaceNames(_lib.PathOf(@"data\PlaceNames.json"), async (_, cancel) =>
        {
            if (!first) return Seattle;
            first = false;
            using (cancel.Register(() => release.TrySetCanceled(cancel))) return await release.Task;
        });
        using var closing = new CancellationTokenSource();
        var pending = places.LookUpAsync(1, 1, closing.Token);
        closing.Cancel();
        Assert.Null(await pending);
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await places.LookUpAsync(47.6097, -122.3422));
    }

    [Fact]
    public void ACorruptCache_IsStartedAfresh()
    {
        Directory.CreateDirectory(_lib.PathOf("data"));
        File.WriteAllText(_lib.PathOf(@"data\PlaceNames.json"), "{ not json");
        var places = Service();
        Assert.Null(places.TryCached(47.6097, -122.3422));
    }

    [Fact]
    public void TheRequest_SaysWhichAppAsks_AndNothingAboutThePerson()
    {
        Assert.Matches(@"^PhotoFinder/[\w.]+ \(\+https://github\.com/kellylford/PhotoFinder\)$", PlaceNames.UserAgent.Replace(AppInfo.Name.Replace(" ", ""), "PhotoFinder"));
    }

    [Theory]
    [InlineData("Photo Finder", "PhotoFinder")]
    [InlineData("Fotó (Finder)", "FotFinder")]
    [InlineData("…", "PhotoFinder")]
    public void TheAppsName_BecomesAValidProductToken(string name, string token) => Assert.Equal(token, PlaceNames.Token(name));

    [Fact]
    public async Task TheQuery_HoldsOnlyTheCoordinatesTheLanguageAndTheFormat()
    {
        await Service().LookUpAsync(47.6097, -122.3422);
        var names = System.Web.HttpUtility.ParseQueryString(_asked.Single().Query).AllKeys.Order();
        Assert.Equal(["accept-language", "addressdetails", "format", "lat", "lon", "zoom"], names);
    }

    [Fact]
    public async Task AFailure_IsNoName_AndIsTriedAgainNextTime()
    {
        var calls = 0;
        var places = new PlaceNames(_lib.PathOf(@"data\PlaceNames.json"), (_, _) =>
            ++calls == 1 ? throw new System.Net.Http.HttpRequestException("Offline") : Task.FromResult(Seattle));
        Assert.Null(await places.LookUpAsync(47.6097, -122.3422));
        Assert.Equal("85 Pike Street, Seattle, Washington, United States", await places.LookUpAsync(47.6097, -122.3422));
        Assert.Equal(2, calls);
        // Nonsense coordinates aren't sent at all.
        Assert.Null(await places.LookUpAsync(double.NaN, 0));
        Assert.Null(await places.LookUpAsync(91, 0));
        Assert.Equal(2, calls);
    }

    private FileRow RowWithCoordinates()
    {
        var path = _lib.AddJpeg("a.jpg");
        var info = new FileInfo(path);
        return FileRow.ForItem(new MediaItem(path, info.Length, info.CreationTime, info.LastWriteTime)
            { Metadata = MediaMetadata.Empty with { Latitude = 47.6097, Longitude = -122.3422 } });
    }

    [WpfFact]
    public async Task GetInfo_WithItOff_SendsNothing_AndShowsCoordinates()
    {
        var window = new InfoWindow(RowWithCoordinates(), new Settings(), () => { }, describeNow: false, placeNames: Service()) { ShowActivated = false };
        window.Show();
        try
        {
            await Task.Delay(500);
            Assert.Empty(_asked);
            var labels = window.GeneralList.Items.Cast<InfoLine>().Select(l => l.Label).ToList();
            Assert.Contains("Coordinates", labels);
            Assert.DoesNotContain("Place Taken", labels);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task GetInfo_WithItOn_AddsThePlaceAboveTheCoordinates_KeepingTheLineYouWereOn()
    {
        var window = new InfoWindow(RowWithCoordinates(), new Settings { LookUpPlaceNames = true }, () => { }, describeNow: false, placeNames: Service())
            { ShowActivated = false };
        window.Show();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!window.GeneralList.Items.Cast<InfoLine>().Any(l => l.Label == "Place Taken") && DateTime.UtcNow < until) await Task.Delay(20);
            var labels = window.GeneralList.Items.Cast<InfoLine>().Select(l => l.Label).ToList();
            Assert.Equal(labels.IndexOf("Coordinates") - 1, labels.IndexOf("Place Taken"));
            Assert.Equal("85 Pike Street, Seattle, Washington, United States (© OpenStreetMap contributors)",
                window.GeneralList.Items.Cast<InfoLine>().Single(l => l.Label == "Place Taken").Value);
            // Focus stayed on the first line, Name.
            Assert.Equal(0, window.GeneralList.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task GetInfo_ANameAlreadyFound_IsThereAsItOpens_AndOneArrivingLater_KeepsTheLineYouAreOn()
    {
        var places = Service();
        await places.LookUpAsync(47.6097, -122.3422);
        var window = new InfoWindow(RowWithCoordinates(), new Settings { LookUpPlaceNames = true }, () => { }, describeNow: false, placeNames: places)
            { ShowActivated = false };
        Assert.Contains(window.GeneralList.Items.Cast<InfoLine>(), l => l.Label == "Place Taken");
        window.Close();

        // A new spot, looked up while you're on a line below where the name goes.
        var slow = new TaskCompletionSource<string>();
        var later = new PlaceNames(_lib.PathOf(@"data\Other.json"), (_, _) => slow.Task);
        var second = new InfoWindow(RowWithCoordinates(), new Settings { LookUpPlaceNames = true }, () => { }, describeNow: false, placeNames: later);
        second.Show();
        try
        {
            var lines = second.GeneralList.Items.Cast<InfoLine>().ToList();
            var below = lines.FindIndex(l => l.Label == "Coordinates") + 1;
            second.GeneralList.SelectedIndex = below;
            second.GeneralList.UpdateLayout();
            ((ListBoxItem)second.GeneralList.ItemContainerGenerator.ContainerFromIndex(below)).Focus();
            var label = lines[below].Label;
            slow.SetResult(Seattle);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!second.GeneralList.Items.Cast<InfoLine>().Any(l => l.Label == "Place Taken") && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.Equal(label, ((InfoLine)second.GeneralList.SelectedItem).Label);
            Assert.True(Keyboard.FocusedElement is ListBoxItem { Content: InfoLine { } focused } && focused.Label == label);
        }
        finally
        {
            second.Close();
        }
    }

    [WpfFact]
    public async Task TurningItOn_AsksFirst_SayingWhatsSentAndToWhom_AndCancelLeavesItOff()
    {
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        using (model)
        {
            await model.StartAsync();
            var window = new MainWindow(model) { ShowActivated = false };
            window.Show();
            var original = QuestionDialog.Ask;
            var asked = new List<string>();
            try
            {
                QuestionDialog.Ask = (_, _, question, details, yes) => { asked.Add($"{question}|{details}|{yes}"); return false; };
                window.SetLookUpPlaceNames(true);
                Assert.False(model.Settings.LookUpPlaceNames);
                Assert.False(window.PlaceNamesItem.IsChecked);
                var question = Assert.Single(asked);
                Assert.StartsWith("Look up place names?|", question);
                Assert.Contains("latitude and longitude, and the language Windows uses so the name comes back in it", question);
                Assert.Contains("nothing about you or the photo", question);
                Assert.Contains("OpenStreetMap's Nominatim", question);
                Assert.EndsWith("|Look Up Place Names", question);

                QuestionDialog.Ask = (_, _, _, _, _) => true;
                window.SetLookUpPlaceNames(true);
                Assert.True(model.Settings.LookUpPlaceNames);
                Assert.True(Settings.Load(_lib.PathOf(@"data\Settings.json")).LookUpPlaceNames);

                // Turning it off doesn't ask.
                QuestionDialog.Ask = (_, _, _, _, _) => throw new InvalidOperationException("Asked to turn it off");
                window.SetLookUpPlaceNames(false);
                Assert.False(model.Settings.LookUpPlaceNames);
            }
            finally
            {
                QuestionDialog.Ask = original;
                window.Close();
            }
        }
    }
}
