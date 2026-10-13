namespace PhotoFinder.Services;

/// <summary>What goes in something public (a bug report) or something that might be attached to one (the log).</summary>
public static class Privacy
{
    /// <summary>The user's folder, which has their Windows user name in it, written as %USERPROFILE%.</summary>
    public static string Redact(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
    }
}
