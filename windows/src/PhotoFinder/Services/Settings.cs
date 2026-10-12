using System.Text.Json;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

public enum ViewMode { List, Gallery }

/// <summary>What the app remembers between runs.</summary>
public sealed class Settings
{
    public ViewMode ViewMode { get; set; } = ViewMode.List;
    /// <summary>The columns shown, in the order they're shown and read, by id. Null until chosen.</summary>
    public List<string>? ColumnOrder { get; set; }

    /// <summary>Columns turned on or off from their defaults, from before columns could be reordered. Read once
    /// to carry the choice over; ColumnOrder replaces it.</summary>
    public Dictionary<string, bool> Columns { get; set; } = [];
    public string SortColumn { get; set; } = "date";
    public bool SortDescending { get; set; } = true;
    /// <summary>Folders added to the library beyond the Pictures library.</summary>
    public List<string> ExtraFolders { get; set; } = [];
    /// <summary>Pictures library folders taken out of the app's library.</summary>
    public List<string> RemovedFolders { get; set; } = [];
    public string DescriptionKind { get; set; } = "accessible";
    /// <summary>Describe each photo with Windows AI when it's arrowed to, and speak the description.</summary>
    public bool AutoDescribe { get; set; } = true;
    /// <summary>Describe the photos in a folder, behind the scenes, when it's added to the library.</summary>
    public bool DescribeInBackground { get; set; }
    /// <summary>Photo > Save Descriptions as Captions: also write each new description into the photo's caption.</summary>
    public CaptionPolicy CaptionPolicy { get; set; } = CaptionPolicy.Never;

    /// <summary>The columns shown, in order, always including Name.</summary>
    public List<string> ShownColumnIds()
    {
        if (ColumnOrder is not null) return Models.Columns.Clean(ColumnOrder);
        // Settings from before reordering: the old on/off choices, in the default order.
        return Models.Columns.Clean(Models.Columns.All
            .Where(c => c.Id == Models.Columns.Name || (Columns.TryGetValue(c.Id, out var visible) ? visible : c.VisibleByDefault))
            .Select(c => c.Id));
    }

    public bool IsColumnVisible(ColumnInfo column) => ShownColumnIds().Contains(column.Id);

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
