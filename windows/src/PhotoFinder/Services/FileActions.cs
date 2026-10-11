using System.Diagnostics;

namespace PhotoFinder.Services;

/// <summary>Export and the other things done with the files themselves. On Windows the library is
/// ordinary files, so Open, Copy and drag use the originals; only Export makes copies.</summary>
public static class FileActions
{
    /// <summary>Copies a file into a folder, never overwriting: "IMG_1.JPG" becomes "IMG_1 2.JPG" and so on.
    /// The copy keeps the original's dates, so it sorts sensibly in File Explorer.</summary>
    public static string ExportInto(string source, string folder)
    {
        var target = UniquePath(folder, Path.GetFileName(source));
        File.Copy(source, target, overwrite: false);
        try
        {
            File.SetCreationTime(target, File.GetCreationTime(source));
            File.SetLastWriteTime(target, File.GetLastWriteTime(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is what matters; its dates are a nicety.
        }
        return target;
    }

    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} {n}{ext}");
        return path;
    }

    /// <summary>Opens files in their default apps.</summary>
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary>Opens File Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            ArgumentList = { "/select,", path },
            UseShellExecute = false,
        })?.Dispose();
}
