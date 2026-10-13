namespace PhotoFinder.Models;

/// <summary>
/// What the gallery says when you move to a photo, chosen in View > Labels > Gallery (#38), as on the Mac: the chosen
/// items in order, joined with ", ". The description isn't one of them; it follows after the usual pause when Describe
/// Photos Automatically is on.
/// </summary>
public static class GalleryLabels
{
    public const string Position = "position";

    /// <summary>The items, in the order Available lists them, with their titles. The ids are the list's column ids where
    /// there's one.</summary>
    public static readonly (string Id, string Title)[] All =
    [
        (Position, "Position"),
        (Columns.Name, "Name"),
        ("kind", "Kind"),
        ("date", "Date Taken"),
        ("dimensions", "Dimensions"),
        ("caption", "Caption"),
        ("keywords", "Keywords"),
        ("rating", "Rating"),
        ("location", "Place Taken"),
    ];

    /// <summary>Position only: "12 of 400", then the description (Kelly, 13 Oct).</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = [Position];

    public static string Title(string id) => All.FirstOrDefault(a => a.Id == id).Title ?? id;

    /// <summary>A usable choice: known items only, each once, the default when nothing usable is left.</summary>
    public static List<string> Clean(IEnumerable<string>? ids)
    {
        var known = All.Select(a => a.Id).ToHashSet();
        var items = (ids ?? []).Where(known.Contains).Distinct().ToList();
        return items.Count > 0 ? items : [.. DefaultOrder];
    }

    /// <summary>The photo as the gallery reads it, for example "12 of 400, IMG_1234.HEIC, Caption: At the beach".
    /// <paramref name="number"/> counts from 1. Items with nothing to say are left out; if that leaves nothing, the name
    /// is read, so moving to a photo never says nothing.</summary>
    public static string Text(FileRow row, int number, int count, IEnumerable<string> items)
    {
        var parts = new List<string>();
        foreach (var id in items)
        {
            // The list's own words for each, so the two read alike ("Caption: …", "Place taken …").
            var value = id == Position ? $"{number} of {count}" : row.SpokenText(id);
            if (value.Length > 0) parts.Add(value);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : row.Name;
    }
}
