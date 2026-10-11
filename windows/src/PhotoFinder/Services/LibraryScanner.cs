using PhotoFinder.Models;

namespace PhotoFinder.Services;

/// <summary>Finds every photo and video under the library's folders. Only the file system is read,
/// which is fast; the properties come later from <see cref="MetadataReader"/>.</summary>
public static class LibraryScanner
{
    /// <summary>Cloud files that aren't on this PC yet (OneDrive's "online-only"). Reading their
    /// properties would download them, so they keep their file dates until someone opens one.</summary>
    public const FileAttributes OnlineOnly = (FileAttributes)0x400000 /* RECALL_ON_DATA_ACCESS */ | FileAttributes.Offline;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Every media file under the roots, once each even when roots overlap.</summary>
    public static List<MediaItem> Scan(IEnumerable<string> roots, CancellationToken cancel = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<MediaItem>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var dir = new DirectoryInfo(root);
            IEnumerable<FileInfo> files;
            try { files = dir.EnumerateFiles("*", Options); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();
                if (!MediaTypes.IsMedia(file.Name) || !seen.Add(file.FullName)) continue;
                try
                {
                    items.Add(new MediaItem(file.FullName, file.Length, file.CreationTime, file.LastWriteTime)
                    {
                        IsOnlineOnly = (file.Attributes & OnlineOnly) != 0,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted or locked between listing and reading; it'll turn up on the next scan.
                }
            }
        }
        return items;
    }

    /// <summary>Removes roots inside other roots, so a folder never appears twice in the sidebar.</summary>
    public static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var full = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return full.Where(r => !full.Any(other => !other.Equals(r, StringComparison.OrdinalIgnoreCase) && IsInside(r, other))).ToList();
    }

    public static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
