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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);
}
