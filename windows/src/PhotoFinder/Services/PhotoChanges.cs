namespace PhotoFinder.Services;

/// <summary>
/// Read-only (#83): whether Photo Finder may change photo files at all. Every writer checks it (MetadataWriter for
/// captions and ratings, XmpFields for the caption and accessibility fields), so nothing touches a photo while it's
/// false, whatever asks. Off at every launch; only File > Read-Only, after asking, turns it on until the app quits.
/// Photo Finder's own files (settings, saved descriptions, its cache) aren't photos and aren't covered.
/// </summary>
public static class PhotoChanges
{
    private static volatile bool _allowed;

    /// <summary>Read on the threads writes run on, so volatile.</summary>
    public static bool Allowed { get => _allowed; set => _allowed = value; }

    /// <summary>Said when a change is asked for while read-only.</summary>
    public const string ReadOnlyMessage = $"{AppInfo.Name} is in Read-Only Mode, so photos can't be changed. To change them, turn off Read-Only Mode in the File menu.";

    /// <summary>Throws <see cref="ReadOnlyException"/> while read-only.</summary>
    public static void Check()
    {
        if (!Allowed) throw new ReadOnlyException();
    }
}

/// <summary>A change to a photo asked for while Photo Finder is read-only.</summary>
public sealed class ReadOnlyException() : InvalidOperationException(PhotoChanges.ReadOnlyMessage);
