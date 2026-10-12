using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace PhotoFinder.Services;

/// <summary>
/// Updates for Photo Finder as installed from PhotoFinder.appinstaller. Windows also checks in the background every
/// few hours and installs an update while Photo Finder isn't running; this is Help > Check for Updates, for now.
/// (Checking at every launch, the .appinstaller's OnLaunch setting, stopped the AI helper's alias from starting, so
/// the package doesn't use it.) Installing follows Microsoft's "Update non-Store published apps from your code".
/// </summary>
public static class Updates
{
    public enum State { NoIdentity, NotFromAppInstaller, UpToDate, Available, Failed }

    public sealed record Result(State State, string? Message = null, Uri? Source = null);

    /// <summary>The installed package's version, or null when Photo Finder isn't running from its package.</summary>
    public static string? PackageVersion
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception)
            {
                // No package identity: a development build or a test run.
                return null;
            }
        }
    }

    /// <summary>The installed package's version, or the build's when Photo Finder isn't running from its package.</summary>
    public static string AppVersion =>
        PackageVersion ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Installed from PhotoFinder.appinstaller, so Windows knows where its updates are.</summary>
    public static bool UpdatesItself
    {
        get
        {
            try
            {
                return Package.Current.GetAppInstallerInfo() is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static async Task<Result> CheckAsync()
    {
        Package package;
        try
        {
            package = Package.Current;
        }
        catch (Exception)
        {
            return new Result(State.NoIdentity);
        }
        Uri? source;
        try
        {
            source = package.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception)
        {
            source = null;
        }
        // Installed from the bundle itself rather than the .appinstaller: Windows doesn't know where updates are.
        if (source is null) return new Result(State.NotFromAppInstaller);
        try
        {
            var result = await package.CheckUpdateAvailabilityAsync();
            return FromAvailability(result.Availability, result.ExtendedError?.Message, source);
        }
        catch (Exception ex)
        {
            return new Result(State.Failed, ex.Message, source);
        }
    }

    internal static Result FromAvailability(PackageUpdateAvailability availability, string? error, Uri source) => availability switch
    {
        PackageUpdateAvailability.Available or PackageUpdateAvailability.Required => new Result(State.Available, Source: source),
        PackageUpdateAvailability.NoUpdates => new Result(State.UpToDate, Source: source),
        _ => new Result(State.Failed, string.IsNullOrWhiteSpace(error) ? "The place updates come from couldn't be reached." : error, source),
    };

    /// <summary>Downloads and installs the update. When it's ready, Windows closes Photo Finder to install it, and
    /// opens it again (it's registered for restart). Returns why it failed, or null.</summary>
    public static async Task<string?> InstallAsync(Uri source)
    {
        RegisterApplicationRestart(null, 0);
        try
        {
            var result = await new PackageManager().AddPackageByAppInstallerFileAsync(
                source, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, null);
            return result.IsRegistered ? null : string.IsNullOrWhiteSpace(result.ErrorText) ? $"Error {result.ExtendedErrorCode?.HResult:X8}." : result.ErrorText;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // MARK: The shared update experience (The-Idea-Place-Projects#12, #85)

    /// <summary>The version people see: major.minor.build. The package's fourth number counts builds.</summary>
    public static string ShortVersion(string version) =>
        Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : version;

    /// <summary>Two versions as people see them, with the build number when that's all that differs.</summary>
    public static (string Running, string Other) Shown(string running, string other) =>
        ShortVersion(running) == ShortVersion(other) && running != other
            ? (running, other)
            : (ShortVersion(running), ShortVersion(other));

    /// <summary>Help's update item, which always says what's running, or what's available once an update is known.</summary>
    public static string MenuTitle(string running, bool available, string? availableVersion)
    {
        if (!available) return $"Check for _Updates (running {ShortVersion(running)})…";
        return availableVersion is null ? "_Update Available…" : $"_Update to {Shown(running, availableVersion).Other} available…";
    }

    /// <summary>Whether to say "updated" on this launch: only when a version ran here before and this one is newer. Never
    /// on a first run or after going back to an older version.</summary>
    public static bool JustUpdated(string? last, string current) =>
        last is not null && Version.TryParse(last, out var before) && Version.TryParse(current, out var now) && now > before;

    /// <summary>The version a .appinstaller file offers (its MainBundle's, or MainPackage's), or null when it doesn't say
    /// one that reads as a version.</summary>
    internal static string? VersionIn(string appInstaller)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Parse(appInstaller).Root;
            var main = root?.Elements().FirstOrDefault(e => e.Name.LocalName is "MainBundle" or "MainPackage");
            var version = main?.Attribute("Version")?.Value ?? root?.Attribute("Version")?.Value;
            return version is not null && Version.TryParse(version, out _) ? version : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether a version is newer than the one running.</summary>
    public static bool IsNewer(string? version, string running) =>
        version is not null && Version.TryParse(version, out var v) && Version.TryParse(running, out var r) && v > r;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The version the .appinstaller at <paramref name="source"/> offers, or null when it can't be read.</summary>
    public static async Task<string?> OfferedVersionAsync(Uri source)
    {
        try
        {
            var text = source.IsFile ? await File.ReadAllTextAsync(source.LocalPath) : await Http.GetStringAsync(source);
            return VersionIn(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Quietly: the check's state and, when there's an update, which version. Nothing is said or shown.</summary>
    public static async Task<(State State, string? Version)> CheckQuietlyAsync()
    {
        var result = await CheckAsync();
        if (result.State != State.Available || result.Source is not { } source) return (result.State, null);
        return (State.Available, await OfferedVersionAsync(source));
    }

    /// <summary>How the update experience reaches the system, replaceable in tests.</summary>
    internal static Func<string?> InstalledVersion { get; set; } = () => PackageVersion;
    internal static Func<bool> SelfUpdating { get; set; } = () => UpdatesItself;
    internal static Func<Task<(State State, string? Version)>> QuietCheck { get; set; } = CheckQuietlyAsync;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);
}
