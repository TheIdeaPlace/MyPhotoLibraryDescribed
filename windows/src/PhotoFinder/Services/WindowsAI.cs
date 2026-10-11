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

    public const uint FirstSize = 2048;

    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _server;
    private int _nextId;

    public static string AliasPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias);

    /// <summary>The helper to start. Installed, it's the one in Photo Finder's own package, started directly: a packaged
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

    public const string NotInstalledMessage =
        "Describe Image uses Windows' on-device AI on Copilot+ PCs, through the Photo Finder AI helper, which isn't installed on this PC.";

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
            return new AIReadiness(false, "Error", $"The Photo Finder AI helper didn't start: {ex.Message}");
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
        if (string.IsNullOrWhiteSpace(line)) return new AIReadiness(false, "Error", "The Photo Finder AI helper didn't answer.");
        try
        {
            var o = JsonNode.Parse(line) as JsonObject;
            var identity = o?["identity"]?.GetValue<bool>() ?? false;
            var state = o?["state"]?.GetValue<string>() ?? "Error";
            var message = o?["message"]?.GetValue<string>() ?? MessageForState(state);
            return new AIReadiness(identity, state, message);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return new AIReadiness(false, "Error", $"The Photo Finder AI helper gave an answer that couldn't be read: {line.Trim()}");
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
    /// so a failure can be tried again smaller.</summary>
    public Task<string> DescribeAsync(Func<uint, Task<byte[]>> jpegAtSize, string kind, CancellationToken cancel = default) =>
        WithRetriesAsync(jpegAtSize, jpeg => DescribeOnceAsync(jpeg, kind, cancel));

    /// <summary>Tries the first size, then smaller ones while the model reports an internal error.
    /// Any other failure (content filter, too much text) is final.</summary>
    internal static async Task<string> WithRetriesAsync(Func<uint, Task<byte[]>> jpegAtSize, Func<byte[], Task<string>> describe)
    {
        var sizes = new[] { FirstSize }.Concat(RetrySizes).ToArray();
        DescriptionException? last = null;
        foreach (var size in sizes)
        {
            var jpeg = await jpegAtSize(size);
            try
            {
                return await describe(jpeg);
            }
            catch (DescriptionException ex) when (ex.Code == "internal_error")
            {
                last = ex;
            }
        }
        throw last!;
    }

    private async Task<string> DescribeOnceAsync(byte[] jpeg, string kind, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            var server = EnsureServer();
            var id = Interlocked.Increment(ref _nextId);
            string? line;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(DescribeTimeout);
            try
            {
                await server.StandardInput.WriteAsync(BuildRequest(id, kind, jpeg) + "\n");
                await server.StandardInput.FlushAsync(timeout.Token);
                line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the helper may still answer this request later, and that answer would
                // then be read as the next one's. A fresh helper keeps answers and requests in step.
                StopServer();
                if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                    throw new DescriptionException("timeout", "Windows took too long to describe this photo. Try again.");
                if (ex is IOException)
                    throw new DescriptionException("internal_error", "The Photo Finder AI helper stopped unexpectedly.");
                throw;
            }
            if (line is null)
            {
                StopServer();
                throw new DescriptionException("internal_error", "The Photo Finder AI helper stopped unexpectedly.");
            }
            try
            {
                return ParseResponse(line, id);
            }
            catch (DescriptionException ex) when (ex.Code == "bad_response")
            {
                StopServer();
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
        if (o is null) throw new DescriptionException("bad_response", $"The Photo Finder AI helper gave an answer that couldn't be read.");
        if (o["id"]?.GetValue<int>() is { } id && id != expectedId && id != 0)
            throw new DescriptionException("bad_response", "The Photo Finder AI helper answered a different request.");
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
        _server = Process.Start(StartInfo("--serve")) ?? throw new DescriptionException("not_installed", NotInstalledMessage);
        // Its stderr is only progress notes ("getting the model ready"); read it so it never fills up and blocks.
        _server.ErrorDataReceived += (_, _) => { };
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
