using Windows.Storage;

namespace PhotoFinder.Services;

/// <summary>The folders that make up the library: the Windows Pictures library (the same folders the
/// Photos app and File Explorer show), plus any folders added in Photo Finder.</summary>
public static class LibraryRoots
{
    public static async Task<List<string>> PicturesLibraryAsync()
    {
        var folders = new List<string>();
        try
        {
            var library = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
            folders.AddRange(library.Folders.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)));
        }
        catch (Exception)
        {
            // Some setups (a redirected or missing library) refuse; the Pictures folder is the fallback.
        }
        if (folders.Count == 0)
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(pictures)) folders.Add(pictures);
        }
        return folders;
    }

    /// <summary>The library's folders after the person's additions and removals.</summary>
    public static List<string> Combine(IEnumerable<string> picturesLibrary, Settings settings)
    {
        var removed = new HashSet<string>(settings.RemovedFolders.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return LibraryScanner.NormalizeRoots(picturesLibrary.Concat(settings.ExtraFolders).Where(f => !removed.Contains(Normalize(f))));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
