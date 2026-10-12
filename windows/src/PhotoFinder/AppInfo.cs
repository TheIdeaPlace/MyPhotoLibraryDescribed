namespace PhotoFinder;

/// <summary>
/// The app's name, in one place. "Photo Finder" is a working name (#34): everything the app says or shows uses
/// <see cref="Name"/>, and scripts\build-msix.ps1 reads it from here for the package's display names. Identifiers that
/// are costly to change are kept separate on purpose: the package identity (TheIdeaPlace.PhotoFinder), the data folder
/// (<see cref="DataFolderName"/>), the helper's photofinder-ai.exe alias and the announcement activity IDs.
/// Also compiled into the AI helper.
/// </summary>
internal static class AppInfo
{
    public const string Name = "Photo Finder";

    /// <summary>The Windows AI helper's name, as people see it.</summary>
    public const string HelperName = Name + " AI helper";

    /// <summary>The folder under %LOCALAPPDATA%. Not the display name: renaming the app mustn't lose saved settings
    /// and descriptions.</summary>
    public const string DataFolderName = "PhotoFinder";

    /// <summary>The GitHub repository, in one place (as the Mac's AppInfo.repository), for an eventual move.</summary>
    public const string Repository = "kellylford/PhotoFinder";

    /// <summary>Where Report a Bug files issues.</summary>
    public const string IssuesUrl = $"https://github.com/{Repository}/issues/new";

    /// <summary>Windows releases are tagged windows-v&lt;version&gt;, as the Mac's are mac-v&lt;version&gt;.</summary>
    public const string ReleaseTagPrefix = "windows-v";

    /// <summary>All the releases.</summary>
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";

    /// <summary>A version's release notes, for See what's new.</summary>
    public static string ReleaseNotesUrl(string version) => $"https://github.com/{Repository}/releases/tag/{ReleaseTagPrefix}{version}";
}
