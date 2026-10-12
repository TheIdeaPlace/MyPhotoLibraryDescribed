using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PhotoFinder.Models;

public enum MediaKind { Image, Video }

/// <summary>Which files count as photos and videos, and how to describe them.</summary>
public static class MediaTypes
{
    /// <summary>Favorites are the photos rated five stars, so they're the same in File Explorer and any
    /// other app that reads ratings.</summary>
    public const int FavoriteRating = 5;

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".heic", ".heif", ".hif", ".avif", ".webp", ".jxr", ".wdp", ".jxl", ".ico",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".3gp", ".3g2", ".mts", ".m2ts", ".mpg", ".mpeg",
    };

    public static bool IsMedia(string path)
    {
        // Photo Finder's own working copy of a photo being saved (XmpFields), never a photo of yours.
        if (Path.GetFileName(path).StartsWith(Services.XmpFields.WorkingCopyPrefix, StringComparison.Ordinal)) return false;
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext) || RawExtensions.Contains(ext);
    }

    public static MediaKind KindOf(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path)) ? MediaKind.Video : MediaKind.Image;

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));

    public static bool IsAnimated(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows saves screenshots to a Screenshots folder (Snipping Tool and Win+PrtScn) and
    /// names them "Screenshot ...".</summary>
    public static bool IsScreenshot(MediaItem item) =>
        item.Kind == MediaKind.Image
        && (item.Name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(item.Folder).Equals("Screenshots", StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows' screen recordings go to Videos\Captures or Videos\Screen Recordings.</summary>
    public static bool IsScreenRecording(MediaItem item)
    {
        if (item.Kind != MediaKind.Video) return false;
        var folder = Path.GetFileName(item.Folder);
        return folder.Equals("Captures", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Screen Recordings", StringComparison.OrdinalIgnoreCase)
            || item.Name.StartsWith("Screen Recording", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPanorama(MediaItem item)
    {
        var m = item.Metadata;
        if (item.Kind != MediaKind.Image || m is null || m.Width == 0 || m.Height == 0) return false;
        var ratio = (double)Math.Max(m.Width, m.Height) / Math.Min(m.Width, m.Height);
        return ratio >= 2.5;
    }

    /// <summary>The Kind column: "Photo", "Screenshot", "Video" and so on.</summary>
    public static string KindDescription(MediaItem item)
    {
        if (item.Kind == MediaKind.Video) return IsScreenRecording(item) ? "Screen Recording" : "Video";
        if (IsScreenshot(item)) return "Screenshot";
        if (IsRaw(item.Path)) return "RAW Photo";
        if (IsAnimated(item.Path)) return "Animated Image";
        if (IsPanorama(item)) return "Panorama";
        return "Photo";
    }

    /// <summary>Windows stores ratings as 1, 25, 50, 75 and 99. Other apps write values in between,
    /// so each star covers a range, the same way File Explorer reads them.</summary>
    public static int StarsFromRating(uint value) => value switch
    {
        0 => 0,
        < 13 => 1,
        < 38 => 2,
        < 63 => 3,
        < 88 => 4,
        _ => 5,
    };

    public static uint RatingFromStars(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 25,
        3 => 50,
        4 => 75,
        _ => 99,
    };

    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"JPEG image", "HEIC File" and so on: the name File Explorer uses. Looking it up is a
    /// registry walk, so names are cached by extension.</summary>
    public static string FileTypeDescription(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return "";
        return TypeNames.GetOrAdd(ext, static e =>
        {
            var info = new SHFILEINFO();
            var ok = SHGetFileInfo(e, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return ok != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szTypeName)
                ? info.szTypeName
                : e.TrimStart('.').ToUpperInvariant() + " File";
        });
    }

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
