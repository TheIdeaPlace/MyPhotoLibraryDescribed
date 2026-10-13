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
    /// <summary>Describe the library's photos behind the scenes (#96): all not described yet, and folders as they're added.</summary>
    public bool DescribeInBackground { get; set; }
    /// <summary>Photo > Save Descriptions in Photos (#62): each new description is also saved in the photo, filling its
    /// caption and alt text when they're empty. Off until the person agrees once.</summary>
    public bool SaveDescriptionsInPhotos { get; set; }
    /// <summary>Photo > Look Up Place Names (#25): Get Info names the place a photo was taken, which sends its coordinates
    /// to OpenStreetMap's Nominatim. Off until the person agrees once.</summary>
    public bool LookUpPlaceNames { get; set; }
    /// <summary>File > Read-Only (#83): nothing changes a photo file while it's on. Every launch starts read-only (Kelly,
    /// 12 Oct): allowing changes lasts until Photo Finder quits, so it's never saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; set; } = true;

    /// <summary>The three-way setting this replaced, read once to carry the choice over: Never becomes off, Only When
    /// There's No Caption and Always become on (Always as the safer "only when empty"). Always Never once read.</summary>
    public CaptionPolicy CaptionPolicy { get; set; } = CaptionPolicy.Never;

    /// <summary>What Save Descriptions in Photos means for a caption or alt text already there.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CaptionPolicy SavePolicy => SaveDescriptionsInPhotos ? CaptionPolicy.WhenEmpty : CaptionPolicy.Never;
    /// <summary>View > Show Description: the Description panel beside the list or gallery. On unless hidden.</summary>
    public bool ShowDescriptionPanel { get; set; } = true;
    public double DescriptionPanelWidth { get; set; } = 320;
    /// <summary>The installed version that last ran, so the first launch after an update can say so
    /// (The-Idea-Place-Projects#12). Only recorded for installed copies.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>When Help's update item last found out whether there's an update, so it looks quietly once a day. Not set
    /// by a check that couldn't reach the feed.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The update that check found, so Help still says "Update to 0.2.0 available…" after a restart.</summary>
    public string? AvailableUpdate { get; set; }

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
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                if (settings.CaptionPolicy != CaptionPolicy.Never)
                {
                    settings.SaveDescriptionsInPhotos = true;
                    settings.CaptionPolicy = CaptionPolicy.Never;
                }
                return settings;
            }
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
