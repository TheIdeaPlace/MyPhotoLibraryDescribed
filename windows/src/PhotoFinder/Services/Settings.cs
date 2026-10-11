using System.Text.Json;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

public enum ViewMode { List, Gallery }

/// <summary>What the app remembers between runs.</summary>
public sealed class Settings
{
    public ViewMode ViewMode { get; set; } = ViewMode.List;
    /// <summary>Columns turned on or off from their defaults, by id.</summary>
    public Dictionary<string, bool> Columns { get; set; } = [];
    public string SortColumn { get; set; } = "date";
    public bool SortDescending { get; set; } = true;
    /// <summary>Folders added to the library beyond the Pictures library.</summary>
    public List<string> ExtraFolders { get; set; } = [];
    /// <summary>Pictures library folders taken out of the app's library.</summary>
    public List<string> RemovedFolders { get; set; } = [];
    public string DescriptionKind { get; set; } = "accessible";

    public bool IsColumnVisible(ColumnInfo column) =>
        column.Id == Models.Columns.Name || (Columns.TryGetValue(column.Id, out var visible) ? visible : column.VisibleByDefault);

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "Settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings fall back to the defaults.
        }
        return new Settings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written beside, then moved over, so a crash mid-write can't leave half a file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saving settings isn't worth interrupting anyone for.
        }
    }
}
