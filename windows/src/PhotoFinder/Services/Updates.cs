using Windows.ApplicationModel;

namespace PhotoFinder.Services;

/// <summary>
/// Updates for Photo Finder as installed from PhotoFinder.appinstaller. Windows also checks in the background every
/// few hours and installs an update while Photo Finder isn't running; this is Help > Check for Updates, for now.
/// (Checking at every launch, the .appinstaller's OnLaunch setting, stops the AI helper's alias from starting, so
/// the package doesn't use it.)
/// </summary>
public static class Updates
{
    public enum State { NotInstalled, UpToDate, Available, Failed }

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

    public static async Task<Result> CheckAsync()
    {
        Package package;
        Uri? source;
        try
        {
            package = Package.Current;
            source = package.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception)
        {
            return new Result(State.NotInstalled);
        }
        if (source is null) return new Result(State.NotInstalled);
        try
        {
            var result = await package.CheckUpdateAvailabilityAsync();
            return result.Availability switch
            {
                PackageUpdateAvailability.Available or PackageUpdateAvailability.Required => new Result(State.Available, Source: source),
                PackageUpdateAvailability.NoUpdates => new Result(State.UpToDate, Source: source),
                _ => new Result(State.Failed, result.ExtendedError?.Message, source),
            };
        }
        catch (Exception ex)
        {
            return new Result(State.Failed, ex.Message, source);
        }
    }
}
