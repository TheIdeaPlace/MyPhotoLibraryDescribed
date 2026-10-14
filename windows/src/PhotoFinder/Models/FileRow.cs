using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;

namespace PhotoFinder.Models;

/// <summary>One row in the Finder-style list: a photo or video, or a container (section, folder, year, month).
/// Photo rows update in place as their details arrive, so the list never rebuilds under the cursor.</summary>
public sealed class FileRow : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private string _description = "";

    /// <summary>The columns the list is showing, in order. Screen readers hear a row as these, in this order,
    /// so what they hear matches what's on screen. Name is read first if it isn't in the list.</summary>
    public static IReadOnlyList<string> SpokenColumns { get; set; } = Columns.DefaultOrder;

    private FileRow(string id, MediaItem? item, SidebarItem? container)
    {
        Id = id;
        Item = item;
        Container = container;
        Refresh();
    }

    public static FileRow ForItem(MediaItem item) => new(item.Path, item, null);

    public static FileRow ForContainer(SidebarItem container) => new(container.Id, null, container);

    public string Id { get; }
    public MediaItem? Item { get; }
    public SidebarItem? Container { get; }
    public bool IsContainer => Container is not null;

    public string Name { get; private set; } = "";
    public DateTime? Date { get; private set; }
    public string DateText { get; private set; } = "";
    public DateTime? Modified { get; private set; }
    public string ModifiedText { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public string Caption { get; private set; } = "";
    public long PixelCount { get; private set; }
    public string Dimensions { get; private set; } = "";
    public double Duration { get; private set; }
    public string DurationText { get; private set; } = "";
    public bool IsFavorite { get; private set; }
    public string FavoriteText => IsFavorite ? "Yes" : "";
    public string Keywords { get; private set; } = "";
    public int Rating { get; private set; }
    public string RatingText { get; private set; } = "";
    public string FileType { get; private set; } = "";
    public long Size { get; private set; }
    public string SizeText { get; private set; } = "";
    public string Orientation { get; private set; } = "";
    public double Megapixels { get; private set; }
    public string MegapixelsText { get; private set; } = "";
    /// <summary>Place Taken: the place's name when it's known (#172), otherwise the coordinates.</summary>
    public string LocationText { get; private set; } = "";
    private string _coordinates = "";
    private string? _placeName;

    /// <summary>Where the photo was taken, or null.</summary>
    public (double Latitude, double Longitude)? Coordinates =>
        Item?.Metadata is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null;

    /// <summary>Shows the place's name in Place Taken (null for the coordinates again). The row doesn't move, and
    /// <paramref name="quietly"/> leaves the spoken name to be told later (the row in focus: hearing it again as a name
    /// arrives would be noise); <see cref="NotifyAccessibleName"/> tells it.</summary>
    public void SetPlaceName(string? name, bool quietly = false)
    {
        if (name == _placeName) return;
        _placeName = name;
        LocationText = name ?? _coordinates;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationText)));
        if (!quietly) NotifyAccessibleName();
    }
    public string Folder { get; private set; } = "";
    /// <summary>A Segoe Fluent Icons glyph for rows that have no thumbnail.</summary>
    public string Glyph { get; private set; } = "";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    /// <summary>Windows AI's description of the photo, once one has been made.</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
            // The Description column can be part of what's read for the row.
            NotifyAccessibleName();
        }
    }

    /// <summary>What a screen reader hears for the row in the list.</summary>
    public string AccessibleName => SpokenWith(SpokenColumns);

    /// <summary>The row as it would be read with these columns in this order (View > Labels' preview).</summary>
    public string SpokenWith(IReadOnlyList<string> columns)
    {
        var order = columns.Contains(Columns.Name) ? columns : columns.Prepend(Columns.Name).ToList();
        var parts = order.Select(SpokenText).Where(t => t.Length > 0).ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : Name;
    }

    /// <summary>What a screen reader hears for the item outside the list (gallery, preview): name, kind, date and caption.</summary>
    public string SpokenDescription
    {
        get
        {
            var parts = new List<string> { Name, Kind };
            if (DateText.Length > 0) parts.Add(DateText);
            if (Caption.Length > 0) parts.Add($"Caption: {Caption}");
            return string.Join(", ", parts);
        }
    }

    /// <summary>The text shown in a column.</summary>
    public string ColumnText(string id) => id switch
    {
        Columns.Name => Name,
        "date" => DateText,
        "kind" => Kind,
        "caption" => Caption,
        "dimensions" => Dimensions,
        "duration" => DurationText,
        "favorite" => FavoriteText,
        "keywords" => Keywords,
        "rating" => RatingText,
        "modified" => ModifiedText,
        "fileType" => FileType,
        "size" => SizeText,
        "orientation" => Orientation,
        "megapixels" => MegapixelsText,
        "location" => LocationText,
        "folder" => Folder,
        "description" => Description,
        _ => "",
    };

    /// <summary>A column's text as it's spoken: labelled where the value alone wouldn't say what it is.</summary>
    internal string SpokenText(string id)
    {
        var text = ColumnText(id);
        if (text.Length == 0) return "";
        return id switch
        {
            Columns.Name => text,
            "caption" => $"Caption: {text}",
            "keywords" => $"Keywords: {text}",
            "favorite" => "Favorite",
            "modified" => $"Modified {text}",
            "location" => $"Place taken {text}",
            "folder" => $"in {text}",
            "description" => $"Description: {text}",
            _ => text,
        };
    }

    /// <summary>Re-reads everything from the item, after its details arrive or change.</summary>
    public void Refresh()
    {
        if (Container is { } c) FillFromContainer(c);
        else if (Item is { } item) FillFromItem(item);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Tells the list the spoken name changed, after the visible columns change.</summary>
    public void NotifyAccessibleName() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));

    /// <summary>"Mar 3, 2024 4:05 PM", or a 24-hour time in cultures without AM and PM.</summary>
    private static string FormatDate(DateTime d)
    {
        var culture = CultureInfo.CurrentCulture;
        var pattern = string.IsNullOrEmpty(culture.DateTimeFormat.AMDesignator) ? "MMM d, yyyy H:mm" : "MMM d, yyyy h:mm tt";
        return d.ToString(pattern, culture);
    }

    private void FillFromItem(MediaItem item)
    {
        var m = item.Metadata ?? MediaMetadata.Empty;
        Name = item.Name;
        Date = item.Date;
        DateText = FormatDate(item.Date);
        Modified = item.Modified;
        ModifiedText = FormatDate(item.Modified);
        Kind = MediaTypes.KindDescription(item);
        Caption = m.Caption;
        Keywords = m.Keywords;
        PixelCount = (long)m.Width * m.Height;
        Dimensions = m.Width > 0 && m.Height > 0 ? $"{m.Width} × {m.Height}" : "";
        Orientation = m.Width == 0 || m.Height == 0 ? "" : m.Width > m.Height ? "Landscape" : m.Width < m.Height ? "Portrait" : "Square";
        Megapixels = PixelCount / 1_000_000.0;
        MegapixelsText = PixelCount > 0 ? $"{Megapixels.ToString("0.0", CultureInfo.CurrentCulture)} MP" : "";
        Duration = item.IsVideo ? m.DurationSeconds : 0;
        DurationText = item.IsVideo && m.DurationSeconds > 0 ? FormatDuration(m.DurationSeconds) : "";
        IsFavorite = item.IsFavorite;
        Rating = m.Rating;
        RatingText = StarsText(m.Rating);
        _coordinates = m.Latitude is { } lat && m.Longitude is { } lon
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0000}, {1:0.0000}", lat, lon) : "";
        // A name found earlier stays while the photo is where it was.
        if (_coordinates.Length == 0) _placeName = null;
        LocationText = _placeName ?? _coordinates;
        FileType = MediaTypes.FileTypeDescription(item.Path);
        Size = item.Size;
        SizeText = FormatSize(item.Size);
        Folder = item.Folder;
        Glyph = item.IsVideo ? "" : "";
    }

    private void FillFromContainer(SidebarItem c)
    {
        Name = c.Title;
        Date = c.Kind switch
        {
            SourceKind.Year => new DateTime(c.Year, 1, 1),
            SourceKind.Month => new DateTime(c.Year, c.Month, 1),
            _ => null,
        };
        Kind = $"{c.ContainerKind}, {c.CountText}";
        FileType = c.ContainerKind;
        Folder = c.FolderPath is { } p ? Path.GetDirectoryName(p) ?? "" : "";
        Glyph = c.Kind switch
        {
            SourceKind.Folder or SourceKind.Year or SourceKind.Section => "",
            SourceKind.Month => "",
            SourceKind.Favorites => "",
            SourceKind.Videos or SourceKind.ScreenRecordings => "",
            SourceKind.Screenshots => "",
            SourceKind.RecentlyAdded => "",
            _ => "",
        };
    }

    public static string StarsText(int stars) => stars switch { 0 => "", 1 => "1 star", _ => $"{stars} stars" };

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size.ToString(size >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    public override string ToString() => AccessibleName;

    public event PropertyChangedEventHandler? PropertyChanged;
}
