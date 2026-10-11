namespace PhotoFinder.Models;

/// <summary>A column in the list. Name is always shown; the rest can be turned on and off.</summary>
public sealed record ColumnInfo(string Id, string Title, bool VisibleByDefault, double Width);

public static class Columns
{
    public const string Name = "name";

    public static readonly ColumnInfo NameColumn = new(Name, "Name", true, 300);

    /// <summary>Every column except Name, in the order they appear.</summary>
    public static readonly IReadOnlyList<ColumnInfo> Optional =
    [
        new("date", "Date Taken", true, 170),
        new("kind", "Kind", true, 130),
        new("caption", "Caption", true, 220),
        new("dimensions", "Dimensions", true, 110),
        new("duration", "Duration", true, 80),
        new("favorite", "Favorite", true, 70),
        new("keywords", "Keywords", false, 160),
        new("rating", "Rating", false, 80),
        new("modified", "Date Modified", false, 170),
        new("fileType", "File Type", false, 120),
        new("size", "Size", false, 90),
        new("orientation", "Orientation", false, 90),
        new("megapixels", "Megapixels", false, 90),
        new("location", "Location", false, 150),
        new("folder", "Folder", false, 260),
    ];

    public static IEnumerable<ColumnInfo> All => Optional.Prepend(NameColumn);

    public static ColumnInfo? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}
