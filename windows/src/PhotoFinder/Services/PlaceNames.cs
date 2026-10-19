using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoFinder.Services;

/// <summary>
/// Photo > Look Up Place Names (#25): Get Info names the place a photo was taken. Finding the name sends the photo's
/// coordinates to OpenStreetMap's Nominatim service (Kelly, 13 Oct: free, rather than a paid Azure Maps account), so it's
/// off until the person agrees once, as on the Mac. Following Nominatim's usage policy: one request a second at most, an
/// identifying User-Agent, never in bulk (when Get Info opens, and for the photos showing when Place Taken is a column or a
/// gallery label, #172, a few at a time), and names already found are kept in the data folder, so a place is sent once.
/// Never throws: no name is no name.
///
/// Nominatim also asks that an app can be switched off "at our request at any time … without requiring a software update"
/// (#128): before asking it, the app reads config/services.json from its own repository on GitHub, at most once a day and
/// only when a place is about to be looked up. Set off there, lookups stop and Get Info says why.
/// </summary>
public sealed class PlaceNames
{
    public const string Question = "Look up place names?";

    public static readonly string Explanation =
        $"To name the place a photo was taken, {AppInfo.Name} sends the photo's latitude and longitude, and the language Windows uses so the name comes back in it (nothing about you or the photo), to OpenStreetMap's Nominatim service: when you open Get Info, and for the photos showing when Place Taken is a list column or a gallery label. Before that, at most once a day, it reads a small settings file from its GitHub repository, so lookups can be paused for everyone if the service asks. Nothing is sent until you turn this on, and you can turn it off at any time in the Photo menu.";

    /// <summary>The credit OpenStreetMap's licence asks for, said with each name.</summary>
    public const string Credit = "© OpenStreetMap contributors";

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "PlaceNames.json");

    /// <summary>Nominatim's reverse lookup, at a level that names a building or a street.</summary>
    private const string Endpoint = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=18&addressdetails=1";

    /// <summary>Nominatim's limit: one request a second, from the whole app.</summary>
    internal static TimeSpan MinimumGap { get; set; } = TimeSpan.FromSeconds(1);

    private static readonly HttpClient Http = MakeClient();

    /// <summary>Who's asking, as Nominatim's policy requires: the app and where to find it, nothing about the person.</summary>
    internal static string UserAgent => $"{Token(AppInfo.Name)}/{Updates.AppVersion} (+https://github.com/{AppInfo.Repository})";

    /// <summary>The app's name as a User-Agent product token: letters and digits only, so a future name with spaces,
    /// accents or brackets (#34) can't stop the header being made.</summary>
    internal static string Token(string name)
    {
        var token = new string(name.Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
        return token.Length > 0 ? token : "PhotoFinder";
    }

    private static HttpClient MakeClient()
    {
        // No cookies: nothing ties one lookup to the next.
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        // Never fails to start: a header Windows won't take is left out rather than breaking Get Info.
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        return client;
    }

    /// <summary>The switch, kept in the repository so it can be changed without an update.</summary>
    public static readonly Uri SwitchUrl = new($"https://raw.githubusercontent.com/{AppInfo.Repository}/main/config/services.json");

    /// <summary>How often the switch is read again.</summary>
    internal static TimeSpan SwitchMaxAge { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Said in Get Info when the switch is off and gives no message of its own.</summary>
    public const string PausedText = "Place names are paused for now.";

    private readonly string _path;
    private readonly string _switchPath;
    private readonly Func<Uri, CancellationToken, Task<string>> _get;
    private readonly Func<CancellationToken, Task<string>> _getSwitch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private Dictionary<string, string>? _cache;
    private DateTime _lastRequest = DateTime.MinValue;
    /// <summary>Nominatim said no (too many requests, or blocked): nothing more is asked until My Photo Library Described starts again.</summary>
    private bool _refused;

    /// <param name="get">Fetches a URL; tests stand in for Nominatim.</param>
    /// <param name="getSwitch">Fetches the switch; tests stand in for GitHub. With a stand-in for Nominatim and none for the
    /// switch, the switch is on and GitHub isn't asked.</param>
    public PlaceNames(string path, Func<Uri, CancellationToken, Task<string>>? get = null, Func<CancellationToken, Task<string>>? getSwitch = null)
    {
        _getSwitch = getSwitch ?? (get is null ? cancel => Http.GetStringAsync(SwitchUrl, cancel) : _ => Task.FromResult(""));
        _path = path;
        _switchPath = Path.Combine(Path.GetDirectoryName(path) ?? "", "ServiceSwitch.json");
        _get = get ?? ((uri, cancel) => Http.GetStringAsync(uri, cancel));
    }

    /// <summary>One for the app, so the one-a-second limit and the cache are shared.</summary>
    public static PlaceNames Shared { get; set; } = new(DefaultPath);

    /// <summary>Set when lookups are switched off remotely (#128): what Get Info says instead of a name.</summary>
    public string? Paused { get; private set; }

    /// <summary>After a failed read, how long before GitHub is asked again, so a network that blocks it doesn't hold up
    /// every Get Info.</summary>
    internal static TimeSpan SwitchRetry { get; set; } = TimeSpan.FromHours(1);

    private DateTime _switchFailed = DateTime.MinValue;

    /// <summary>The switch as last read: on unless it said off. Read again when older than a day (or saved with a clock that
    /// was ahead); if it can't be read, the last copy stands, and with none, lookups are on.</summary>
    private async Task<bool> SwitchedOnAsync(CancellationToken cancel)
    {
        var saved = ReadSwitch();
        var age = saved is { } s ? DateTime.UtcNow - s.Read : TimeSpan.MaxValue;
        if ((age < TimeSpan.Zero || age > SwitchMaxAge) && DateTime.UtcNow - _switchFailed > SwitchRetry)
        {
            try
            {
                var json = await _getSwitch(cancel).ConfigureAwait(false);
                if (SwitchFrom(json) is { } now)
                {
                    saved = (DateTime.UtcNow, now.On, now.Message);
                    WriteSwitch(saved.Value);
                }
                else
                {
                    _switchFailed = DateTime.UtcNow;
                    Log.Write("places", "The service switch wasn't a settings file; the last setting stands");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                _switchFailed = DateTime.UtcNow;
                Log.Error("places", "The service switch couldn't be read; the last setting stands", ex);
            }
        }
        var on = saved?.On ?? true;
        Paused = on ? null : saved?.Message is { Length: > 0 } message ? message : PausedText;
        if (!on) Log.Write("places", "Place names are switched off remotely; nothing is looked up");
        return on;
    }

    /// <summary>placeNames.windows from config/services.json: whether it's on, and what to say when it isn't. Null when the
    /// file isn't one (so a sign-in page can't switch anything off).</summary>
    internal static (bool On, string Message)? SwitchFrom(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["placeNames"] is not JsonObject places
                || places["windows"] is not JsonObject windows) return null;
            // A slip when switching it off ("false" in quotes) mustn't read as on: anything but true or false isn't a setting.
            bool on;
            if (windows["enabled"] is null) on = true;
            else if (windows["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b)) on = b;
            else return null;
            var message = windows["message"] is JsonValue m && m.TryGetValue<string>(out var t) ? t.Trim() : "";
            return (on, message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private (DateTime Read, bool On, string Message)? ReadSwitch()
    {
        try
        {
            if (!File.Exists(_switchPath)) return null;
            if (JsonNode.Parse(File.ReadAllText(_switchPath)) is not JsonObject o
                || o["read"] is not JsonValue r || !r.TryGetValue<DateTime>(out var read)
                || o["on"] is not JsonValue n || !n.TryGetValue<bool>(out var on)) return null;
            return (read.ToUniversalTime(), on, o["message"] is JsonValue m && m.TryGetValue<string>(out var t) ? t : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private void WriteSwitch((DateTime Read, bool On, string Message) value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_switchPath)!);
            // Written whole, then swapped in, so a crash or a second copy of the app never leaves half a file.
            var temp = $"{_switchPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, new JsonObject { ["read"] = value.Read, ["on"] = value.On, ["message"] = value.Message }.ToJsonString());
            File.Move(temp, _switchPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read again next time.
        }
    }

    /// <summary>A name already found for this spot, without asking anyone, so Get Info can show it as it opens.</summary>
    public string? TryCached(double latitude, double longitude) =>
        Cached(Key(latitude, longitude)) is { Length: > 0 } name ? name : null;

    /// <summary>The cache's key: about a metre (5 decimal places), so the same spot is sent once.</summary>
    internal static string Key(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.00000},{longitude:0.00000}");

    /// <summary>The place's name ("Pike Place Market, Seattle, Washington, United States"), or null. Asks Nominatim only
    /// when it isn't already known.</summary>
    public async Task<string?> LookUpAsync(double latitude, double longitude, CancellationToken cancel = default) =>
        (await LookUpWithResultAsync(latitude, longitude, cancel)).Name;

    /// <summary>How a lookup went, for the place-name queue (#172): known already (nothing sent), asked and answered (a
    /// name or none), failed (offline, an answer that wasn't Nominatim's: worth trying again later), or stopped (switched
    /// off remotely, or Nominatim refused: nothing about the photo).</summary>
    internal enum Outcome { Known, Answered, Failed, Stopped }

    /// <summary>The name, and how the lookup went.</summary>
    internal async Task<(string? Name, Outcome Outcome)> LookUpWithResultAsync(double latitude, double longitude, CancellationToken cancel = default)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) return (null, Outcome.Known);
        // Said only for a lookup the switch stopped, never for a spot known to have no name.
        Paused = null;
        var key = Key(latitude, longitude);
        if (Cached(key) is { } known) return (known.Length > 0 ? known : null, Outcome.Known);
        if (_refused) return (null, Outcome.Stopped);
        try
        {
            await _gate.WaitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            return (null, Outcome.Known);
        }
        try
        {
            // Another Get Info may have found it while this waited.
            if (Cached(key) is { } found) return (found.Length > 0 ? found : null, Outcome.Known);
            if (_refused) return (null, Outcome.Stopped);
            // Switched off remotely: nothing goes to Nominatim. A name already found is still shown (above).
            if (!await SwitchedOnAsync(cancel).ConfigureAwait(false)) return (null, Outcome.Stopped);
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
            return (name, Outcome.Answered);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Get Info closed.
            return (null, Outcome.Known);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
        {
            // Nominatim's way of saying stop: respected for the rest of the session.
            _refused = true;
            Log.Error("places", "Nominatim refused place-name lookups; none more until My Photo Library Described starts again", ex);
            return (null, Outcome.Stopped);
        }
        catch (Exception ex)
        {
            // Offline, refused or unreadable: not remembered, so it's tried again next time.
            Log.Error("places", "A place name couldn't be looked up", ex);
            return (null, Outcome.Failed);
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
                // A temporary name of its own, so two copies of My Photo Library Described can't write over each other's.
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
