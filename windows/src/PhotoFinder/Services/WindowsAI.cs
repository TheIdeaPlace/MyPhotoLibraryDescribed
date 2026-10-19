using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PhotoFinder.Services;

/// <summary>Why a description couldn't be made, with the helper's code (see PhotoFinderAI's Protocol.cs).</summary>
public sealed class DescriptionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What the helper said about the model.</summary>
public sealed record AIReadiness(bool Installed, string State, string? Message)
{
    /// <summary>The helper's version and protocol, as it reported them, for Check System (#94).</summary>
    public string? HelperVersion { get; init; }
    public int? Protocol { get; init; }

    /// <summary>The ready state of the other Windows AI features, by name ("textRecognition", "languageModel"), as the
    /// helper reported them.</summary>
    public IReadOnlyDictionary<string, string> Components { get; init; } = new Dictionary<string, string>();

    /// <summary>Ready now, or can be made ready (possibly by downloading the model).</summary>
    public bool Usable => Installed && State is "Ready" or "NotReady";
}

/// <summary>
/// Describes photos with Windows' on-device image description model (Microsoft.Windows.AI.Imaging,
/// on Copilot+ PCs). Windows only lets a packaged app use the model, so the describing happens in
/// PhotoFinderAI, a small packaged helper started through the command its package adds
/// (photofinder-ai), and this talks to it in JSON lines, the same design as Image Description Toolkit.
/// </summary>
public sealed class WindowsAI : IDisposable
{
    public const string Alias = "photofinder-ai.exe";

    /// <summary>The description kinds, in order: the first is the default.</summary>
    public static readonly (string Id, string Title)[] Kinds =
    [
        ("accessible", "Accessible"),
        ("detailed", "Detailed"),
        ("brief", "Brief"),
        ("diagram", "Diagram"),
    ];

    /// <summary>Long edges to try again at after an internal error. Some photos that fail at full size
    /// describe fine smaller, and trying the same size again only fails again (found in IDT).</summary>
    public static readonly uint[] RetrySizes = [1024, 512];

    /// <summary>The long edge sent first. It was 2048, but at 2048 Windows' model never answers for a whole kind of photo
    /// (Kelly's iPhone backup, 19 Oct: the "…_singular_display_fullPicture.heic" files, a third of the library), while at
    /// 1536 the same photos are described in about 7 seconds, and other photos as well as before.</summary>
    public const uint FirstSize = 1536;

    /// <summary>How long a request may take. A photo usually takes 5 to 15 seconds, the first after a start a little
    /// more. It was 3 minutes, but Windows' model can hang on a particular photo and never answer (Kelly's PC, 19 Oct):
    /// each start then sat silent on that photo for 3 minutes.</summary>
    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(60);
    /// <summary>The first request a newly started helper answers also loads Windows' model (or Phi Silica), which can take
    /// a while just after Windows starts or updates, so it gets longer.</summary>
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Whether the helper running now has answered a request yet.</summary>
    private bool _serverAnswered;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _server;
    private int _nextId;

    public static string AliasPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias);

    /// <summary>The helper to start. Installed, it's the one in My Photo Library Described's own package, started directly: a packaged
    /// app's child runs with the package's identity, and this can't pick up another package's photofinder-ai.exe
    /// alias (an old test install, or a development registration). If it somehow starts without identity, it
    /// relaunches itself through the alias. Otherwise (development), the alias.</summary>
    public static string HelperPath => PackagedHelper ?? AliasPath;

    private static readonly string? PackagedHelper = FindPackagedHelper();

    private static string? FindPackagedHelper()
    {
        try
        {
            var path = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "AI", "PhotoFinderAI.exe");
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            // No package identity: a development build or a test run.
            return null;
        }
    }

    public static bool IsInstalled => File.Exists(HelperPath);

    public static readonly string NotInstalledMessage =
        $"Describe Image uses Windows' on-device AI on Copilot+ PCs, through the {AppInfo.HelperName}, which isn't installed on this PC.";

    private static AIReadiness? _ready;

    /// <summary>Whether the model can be used, from the helper's --check. Once it's Ready, that's remembered
    /// for the session rather than starting the helper again for every Get Info.</summary>
    public static async Task<AIReadiness> CheckAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled) return new AIReadiness(false, "NotInstalled", NotInstalledMessage);
        if (_ready is not null) return _ready;
        try
        {
            var line = await RunOnceAsync("--check", CheckTimeout, cancel);
            var readiness = ParseReadiness(line);
            if (readiness.State == "Ready") _ready = readiness;
            return readiness;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AIReadiness(false, "Error", $"The {AppInfo.HelperName} didn't start: {ex.Message}");
        }
    }

    /// <summary>Whether Get Info offers follow-up questions (#24), from the helper's --details: Windows granted this
    /// copy Phi Silica, and has it or can download it. Not from an older helper, which doesn't say, or one that
    /// didn't answer.</summary>
    public static bool OffersFollowUpQuestions(AIReadiness? details) =>
        details?.Components is { } c
        && c.TryGetValue("languageModelAccess", out var access) && access is "Available" or "AvailableWithoutToken"
        && c.TryGetValue("languageModel", out var state) && state is "Ready" or "NotReady";

    /// <summary>The helper's --details for Check System (#94): its readiness plus the other Windows AI features' states.
    /// Separate from --check, which every description depends on, so asking about Phi Silica can't hold describing up.
    /// Not remembered: Check System shows how things are now. An older helper without it gives no component states.</summary>
    public static async Task<AIReadiness?> DetailsAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled) return null;
        try
        {
            return ParseReadiness(await RunOnceAsync("--details", CheckTimeout, cancel));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Gets the model ready, which the first time can mean a download of several minutes.</summary>
    public static async Task<AIReadiness> PrepareAsync(CancellationToken cancel = default)
    {
        var line = await RunOnceAsync("--prepare", PrepareTimeout, cancel);
        return ParseReadiness(line);
    }

    internal static AIReadiness ParseReadiness(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new AIReadiness(false, "Error", $"The {AppInfo.HelperName} didn't answer.");
        try
        {
            var o = JsonNode.Parse(line) as JsonObject;
            var identity = o?["identity"]?.GetValue<bool>() ?? false;
            var state = o?["state"]?.GetValue<string>() ?? "Error";
            var message = o?["message"]?.GetValue<string>() ?? MessageForState(state);
            var components = new Dictionary<string, string>();
            if (o?["components"] is JsonObject parts)
                foreach (var (name, value) in parts)
                    if (value is JsonValue v && v.TryGetValue<string>(out var text)) components[name] = text;
            return new AIReadiness(identity, state, message)
            {
                HelperVersion = o?["version"] is JsonValue ver && ver.TryGetValue<string>(out var version) && version.Length > 0 ? version : null,
                Protocol = o?["protocol"] is JsonValue p && p.TryGetValue<int>(out var protocol) ? protocol : null,
                Components = components,
            };
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return new AIReadiness(false, "Error", $"The {AppInfo.HelperName} gave an answer that couldn't be read: {line.Trim()}");
        }
    }

    internal static string? MessageForState(string state) => state switch
    {
        "Ready" or "NotReady" => null,
        "NotSupportedOnCurrentSystem" => "Windows' image description needs a Copilot+ PC, with an NPU, and a recent version of Windows 11.",
        "DisabledByUser" => "Windows' AI features are turned off on this PC. They can be turned on in Settings, Privacy & security.",
        _ => $"Windows' image description isn't available on this PC ({state}).",
    };

    /// <summary>Describes a photo. <paramref name="jpegAtSize"/> makes the JPEG to send at a given longest side,
    /// so a failure can be tried again smaller.
    /// <paramref name="drop"/> gives the request up only until it's sent: stopping it after that would mean a fresh helper
    /// and a model reload, which costs more than letting it finish.</summary>
    /// <param name="retry">False sends it once: for a picture that's the same at every size (Check AI Description's
    /// sample).</param>
    public async Task<string> DescribeAsync(Func<uint, Task<byte[]>> jpegAtSize, string kind, CancellationToken cancel = default,
        CancellationToken drop = default, bool retry = true) =>
        (await DescribeTimedAsync(jpegAtSize, kind, cancel, drop, retry)).Text;

    /// <summary><see cref="DescribeAsync"/>, with how long Windows took over the attempt that worked (not the wait for the
    /// helper), as saved with the description (#199).</summary>
    public Task<Described> DescribeTimedAsync(Func<uint, Task<byte[]>> jpegAtSize, string kind, CancellationToken cancel = default,
        CancellationToken drop = default, bool retry = true) =>
        CountedTimedAsync(DescribeStats.Session, () => WithRetriesAsync(jpegAtSize, jpeg => DescribeOnceAsync(jpeg, kind, cancel, drop), retry),
            cancel, drop);

    /// <summary>A photo was described, by anything: background describing waiting for Windows AI starts on it.</summary>
    public static event Action? DescribedOne;

    /// <summary>One photo's outcome, counted once for Check System however many sizes were tried: how long the attempt
    /// that worked took (not the wait for the helper), or the last failure.</summary>
    internal static async Task<string> CountedAsync(DescribeStats stats, Func<Task<(string Text, TimeSpan Took)>> describe,
        CancellationToken cancel, CancellationToken drop) =>
        (await CountedTimedAsync(stats, describe, cancel, drop)).Text;

    private static async Task<Described> CountedTimedAsync(DescribeStats stats, Func<Task<(string Text, TimeSpan Took)>> describe,
        CancellationToken cancel, CancellationToken drop)
    {
        string text;
        TimeSpan took;
        try
        {
            (text, took) = await describe();
            stats.Described(took);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested || drop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The helper's own answers are said as they are; anything else only by its type, since its message can name a
            // folder (the helper's path, in the user's folder) and Check System's report is public.
            if (ex is DescriptionException d) stats.Failed(d.Code, d.Message);
            else stats.Failed(ex.GetType().Name, "Unexpected; the log has the details");
            throw;
        }
        // After the description is counted, and outside it: whoever's listening can't make it a failure.
        try { DescribedOne?.Invoke(); }
        catch (Exception ex) { Log.Error("describe", "A listener for finished descriptions failed", ex); }
        return new(text, took);
    }

    /// <summary>Tries the first size, then smaller ones while the model reports an internal error or doesn't answer in
    /// time: a photo it hangs on at one size can be fine smaller (19 Oct). Any other failure (content filter, too much
    /// text) is final.</summary>
    internal static async Task<T> WithRetriesAsync<T>(Func<uint, Task<byte[]>> jpegAtSize, Func<byte[], Task<T>> describe, bool retry = true)
    {
        var sizes = retry ? new[] { FirstSize }.Concat(RetrySizes).ToArray() : [FirstSize];
        DescriptionException? last = null;
        foreach (var size in sizes)
        {
            var jpeg = await jpegAtSize(size);
            try
            {
                return await describe(jpeg);
            }
            // Never two timeouts in a row: each is a minute's wait. (An internal error between them resets this.)
            catch (DescriptionException ex) when (ex.Code == "internal_error" || (ex.Code == "timeout" && last?.Code != "timeout"))
            {
                last = ex;
            }
        }
        throw last!;
    }

    private Task<(string Text, TimeSpan Took)> DescribeOnceAsync(byte[] jpeg, string kind, CancellationToken cancel, CancellationToken drop) =>
        SendAsync(id => BuildRequest(id, kind, jpeg), "Windows took too long to describe this photo. Try again.", cancel, drop);

    /// <summary>A follow-up question about a photo (#24), answered by Windows' text model (Phi Silica) from its description
    /// and any text read from it, not by looking at the photo again.</summary>
    public async Task<string> AskAsync(string description, string text, string question,
        IReadOnlyList<(string Question, string Answer)> history, CancellationToken cancel = default) =>
        (await SendAsync(id => BuildAskRequest(id, description, text, question, history), "Windows took too long to answer. Try again.", cancel, default)).Text;

    internal static string BuildAskRequest(int id, string description, string text, string question, IReadOnlyList<(string Question, string Answer)> history) =>
        new JsonObject
        {
            ["id"] = id,
            ["op"] = "ask",
            ["description"] = description,
            ["text"] = text,
            ["question"] = question,
            ["history"] = new JsonArray(history.Select(h => (JsonNode)new JsonObject { ["question"] = h.Question, ["answer"] = h.Answer }).ToArray()),
        }.ToJsonString();

    /// <summary>One request to the helper and its answer, in turn with any other, and how long it took once sent.</summary>
    private async Task<(string Text, TimeSpan Took)> SendAsync(Func<int, string> build, string tooLong, CancellationToken cancel, CancellationToken drop)
    {
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, drop))
            await _gate.WaitAsync(waiting.Token);
        try
        {
            drop.ThrowIfCancellationRequested();
            var server = EnsureServer();
            var id = Interlocked.Increment(ref _nextId);
            string? line;
            var started = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(_serverAnswered ? DescribeTimeout : FirstRequestTimeout);
            try
            {
                await server.StandardInput.WriteAsync(build(id) + "\n");
                await server.StandardInput.FlushAsync(timeout.Token);
                line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the helper may still answer this request later, and that answer would
                // then be read as the next one's. A fresh helper keeps answers and requests in step.
                StopServer();
                if (!cancel.IsCancellationRequested) Log.Error("helper", "A request to the helper failed; it was restarted", ex);
                if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                    throw new DescriptionException("timeout", tooLong);
                if (ex is IOException)
                    throw new DescriptionException("internal_error", $"The {AppInfo.HelperName} stopped unexpectedly.");
                throw;
            }
            if (line is null)
            {
                Log.Write("helper", "The helper stopped without answering");
                StopServer();
                throw new DescriptionException("internal_error", $"The {AppInfo.HelperName} stopped unexpectedly.");
            }
            _serverAnswered = true;
            try
            {
                return (ParseResponse(line, id), started.Elapsed);
            }
            catch (DescriptionException ex) when (ex.Code == "bad_response")
            {
                Log.Write("helper", $"Unreadable answer from the helper: {ex.Message}");
                StopServer();
                throw;
            }
            catch (DescriptionException ex)
            {
                Log.Write("helper", $"The helper answered {ex.Code}: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildRequest(int id, string kind, byte[] jpeg) =>
        new JsonObject { ["id"] = id, ["kind"] = kind, ["mime"] = "image/jpeg", ["image"] = Convert.ToBase64String(jpeg) }.ToJsonString();

    /// <summary>The description from a response line, or a <see cref="DescriptionException"/> with the helper's code.</summary>
    internal static string ParseResponse(string line, int expectedId)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { o = null; }
        if (o is null) throw new DescriptionException("bad_response", $"The {AppInfo.HelperName} gave an answer that couldn't be read.");
        if (o["id"]?.GetValue<int>() is { } id && id != expectedId && id != 0)
            throw new DescriptionException("bad_response", $"The {AppInfo.HelperName} answered a different request.");
        if (o["ok"]?.GetValue<bool>() == true)
            return (o["text"]?.GetValue<string>() ?? "").Trim();
        var code = o["code"]?.GetValue<string>() ?? "internal_error";
        var message = o["message"]?.GetValue<string>() ?? "Windows couldn't describe this photo.";
        throw new DescriptionException(code, message);
    }

    private Process EnsureServer()
    {
        if (_server is { HasExited: false }) return _server;
        _server?.Dispose();
        _serverAnswered = false;
        _server = Process.Start(StartInfo("--serve")) ?? throw new DescriptionException("not_installed", NotInstalledMessage);
        // Its stderr is only notes ("getting the model ready", "trying once more with a new model"): read so it never
        // fills up and blocks, and kept in the log, where they explain a slow or failed description.
        _server.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("helper", e.Data.Trim()); };
        _server.BeginErrorReadLine();
        return _server;
    }

    private void StopServer()
    {
        if (_server is null) return;
        try
        {
            // Killed outright rather than waited for: this runs on the UI thread, and a helper partway
            // through a description would hold it up.
            _server.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
        _server.Dispose();
        _server = null;
    }

    private static ProcessStartInfo StartInfo(string mode)
    {
        var utf8 = new UTF8Encoding(false);
        return new ProcessStartInfo(HelperPath, mode)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
    }

    private static async Task<string?> RunOnceAsync(string mode, TimeSpan timeout, CancellationToken cancel)
    {
        using var process = Process.Start(StartInfo(mode)) ?? throw new InvalidOperationException(NotInstalledMessage);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            await stderr;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            if (cancel.IsCancellationRequested) throw;
            return null;
        }
    }

    public void Dispose()
    {
        StopServer();
        _gate.Dispose();
    }
}
