using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoFinder.Services;

/// <summary>
/// Photo > Look Up Place Names (#25): Get Info names the place a photo was taken. Finding the name sends the photo's
/// coordinates to OpenStreetMap's Nominatim service (Kelly, 13 Oct: free, rather than a paid Azure Maps account), so it's
/// off until the person agrees once, as on the Mac. Following Nominatim's usage policy: one request a second at most, an
/// identifying User-Agent, never in bulk (only when Get Info opens), and names already found are kept in the data folder,
/// so a place is sent once. Never throws: no name is no name.
/// </summary>
public sealed class PlaceNames
{
    public const string Question = "Look up place names?";

    public static readonly string Explanation =
        $"To name the place a photo was taken, {AppInfo.Name} sends the photo's coordinates (its latitude and longitude, and the language Windows uses, so the name comes back in it; nothing else) to OpenStreetMap's Nominatim service when you open Get Info. Nothing is sent until you turn this on, and you can turn it off at any time in the Photo menu.";

    /// <summary>The credit OpenStreetMap's licence asks for, said with each name.</summary>
    public const string Credit = "© OpenStreetMap contributors";

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "PlaceNames.json");

    /// <summary>Nominatim's reverse lookup, at a level that names a building or a street.</summary>
    private const string Endpoint = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=18&addressdetails=1";

    /// <summary>Nominatim's limit: one request a second, from the whole app.</summary>
    internal static TimeSpan MinimumGap { get; set; } = TimeSpan.FromSeconds(1);

    private static readonly HttpClient Http = MakeClient();

    /// <summary>Who's asking, as Nominatim's policy requires: the app and where to find it, nothing about the person.</summary>
    internal static string UserAgent => $"{AppInfo.Name.Replace(" ", "")}/{Updates.AppVersion} (+https://github.com/{AppInfo.Repository})";

    private static HttpClient MakeClient()
    {
        // No cookies: nothing ties one lookup to the next.
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private readonly string _path;
    private readonly Func<Uri, CancellationToken, Task<string>> _get;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private Dictionary<string, string>? _cache;
    private DateTime _lastRequest = DateTime.MinValue;
    /// <summary>Nominatim said no (too many requests, or blocked): nothing more is asked until Photo Finder starts again.</summary>
    private bool _refused;

    /// <param name="get">Fetches a URL; tests stand in for Nominatim.</param>
    public PlaceNames(string path, Func<Uri, CancellationToken, Task<string>>? get = null)
    {
        _path = path;
        _get = get ?? ((uri, cancel) => Http.GetStringAsync(uri, cancel));
    }

    /// <summary>One for the app, so the one-a-second limit and the cache are shared.</summary>
    public static PlaceNames Shared { get; set; } = new(DefaultPath);

    /// <summary>A name already found for this spot, without asking anyone, so Get Info can show it as it opens.</summary>
    public string? TryCached(double latitude, double longitude) =>
        Cached(Key(latitude, longitude)) is { Length: > 0 } name ? name : null;

    /// <summary>The cache's key: about a metre (5 decimal places), so the same spot is sent once.</summary>
    internal static string Key(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.00000},{longitude:0.00000}");

    /// <summary>The place's name ("Pike Place Market, Seattle, Washington, United States"), or null. Asks Nominatim only
    /// when it isn't already known.</summary>
    public async Task<string?> LookUpAsync(double latitude, double longitude, CancellationToken cancel = default)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) return null;
        var key = Key(latitude, longitude);
        if (Cached(key) is { } known) return known.Length > 0 ? known : null;
        if (_refused) return null;
        try
        {
            await _gate.WaitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            // Another Get Info may have found it while this waited.
            if (Cached(key) is { } found) return found.Length > 0 ? found : null;
            if (_refused) return null;
            var wait = _lastRequest + MinimumGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel);
            _lastRequest = DateTime.UtcNow;
            var language = CultureInfo.CurrentUICulture.Name;
            var uri = new Uri(string.Create(CultureInfo.InvariantCulture,
                $"{Endpoint}&lat={latitude:0.00000}&lon={longitude:0.00000}&accept-language={Uri.EscapeDataString(language.Length > 0 ? language : "en")}"));
            // Off the UI thread from here: writing the cache file needn't hold it up.
            var json = await _get(uri, cancel).ConfigureAwait(false);
            // Not Nominatim's answer (a hotel Wi-Fi sign-in page, say): not remembered, so it's asked again later.
            if (!IsAnswer(json)) throw new FormatException("The answer wasn't Nominatim's.");
            var name = NameFrom(json);
            // A place with no name (the open sea) is remembered too, so it isn't asked again.
            Remember(key, name ?? "");
            return name;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Get Info closed.
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
        {
            // Nominatim's way of saying stop: respected for the rest of the session.
            _refused = true;
            Log.Error("places", "Nominatim refused place-name lookups; none more until Photo Finder starts again", ex);
            return null;
        }
        catch (Exception ex)
        {
            // Offline, refused or unreadable: not remembered, so it's tried again next time.
            Log.Error("places", "A place name couldn't be looked up", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A JSON object, as Nominatim always answers (with "error" when there's no place).</summary>
    internal static bool IsAnswer(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject; }
        catch (JsonException) { return false; }
    }

    /// <summary>The name as the Mac shows it: the place, then the town, the state and the country, without repeats.</summary>
    internal static string? NameFrom(string json)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
        if (o is null || o["error"] is not null) return null;
        var address = o["address"] as JsonObject;
        string? Get(JsonObject? from, params string[] names) =>
            names.Select(n => from?[n] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null)
                .FirstOrDefault(s => s is not null);
        var place = Get(o, "name");
        if (place is null && Get(address, "road") is { } road)
            place = Get(address, "house_number") is { } number ? $"{number} {road}" : road;
        var parts = new List<string>();
        foreach (var part in new[]
                 {
                     place,
                     Get(address, "city", "town", "village", "hamlet", "municipality", "suburb"),
                     Get(address, "state", "province", "region", "county"),
                     Get(address, "country"),
                 })
            if (part is not null && !parts.Contains(part, StringComparer.OrdinalIgnoreCase)) parts.Add(part);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private string? Cached(string key)
    {
        lock (_cacheGate)
        {
            _cache ??= Load();
            return _cache.TryGetValue(key, out var name) ? name : null;
        }
    }

    private void Remember(string key, string name)
    {
        lock (_cacheGate)
        {
            _cache ??= Load();
            _cache[key] = name;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // A temporary name of its own, so two copies of Photo Finder can't write over each other's.
                var temp = $"{_path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for this session; written next time.
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
