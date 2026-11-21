namespace PhotoFinder.Models;

/// <summary>
/// What the gallery says when you move to a photo, chosen in View > Labels > Gallery (#38), as on the Mac: the chosen
/// items in order, joined with ", ". Description is one of them (#247): a photo's saved description is read in its
/// place, straight away, so it can come before or after the position. A photo not described yet is read without it,
/// and Describe Photos Automatically says the description once it's ready. Without Description chosen, the gallery
/// doesn't read descriptions at all (the Description panel still shows them).
/// </summary>
public static class GalleryLabels
{
    public const string Position = "position";
    public const string Description = "description";

    /// <summary>Settings saved from this version on have Description as a gallery label (#247).</summary>
    public const int Version = 2;

    /// <summary>The items, in the order Available lists them, with their titles. The ids are the list's column ids where
    /// there's one.</summary>
    public static readonly (string Id, string Title)[] All =
    [
        (Position, "Position"),
        (Description, "Description"),
        (Columns.Name, "Name"),
        ("kind", "Kind"),
        ("date", "Date Taken"),
        ("dimensions", "Dimensions"),
        ("caption", "Caption"),
        ("keywords", "Keywords"),
        ("rating", "Rating"),
        ("location", "Place Taken"),
    ];

    /// <summary>Position, then the description: "12 of 400, A dog on a beach" (Kelly, 13 Oct; Description a label,
    /// 21 Nov, #247).</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = [Position, Description];

    /// <summary>A choice saved before Description was a label (#247). The description was always read then, after the
    /// labels, so it's added at the end: what's heard stays the same, and it can now be moved or removed.</summary>
    public static List<string>? Migrate(List<string>? ids, int fromVersion) =>
        ids is null || fromVersion >= Version || ids.Contains(Description) ? ids : [.. ids, Description];

    public static string Title(string id) => All.FirstOrDefault(a => a.Id == id).Title ?? id;

    /// <summary>A usable choice: known items only, each once, the default when nothing usable is left.</summary>
    public static List<string> Clean(IEnumerable<string>? ids)
    {
        var known = All.Select(a => a.Id).ToHashSet();
        var items = (ids ?? []).Where(known.Contains).Distinct().ToList();
        return items.Count > 0 ? items : [.. DefaultOrder];
    }

    /// <summary>The photo as the gallery reads it, for example "12 of 400, IMG_1234.HEIC, Caption: At the beach".
    /// <paramref name="number"/> counts from 1; <paramref name="description"/> is its saved description, read for
    /// Description, or null when there's none. Items with nothing to say are left out; if that leaves nothing, the name
    /// is read, so moving to a photo never says nothing.</summary>
    public static string Text(FileRow row, int number, int count, IEnumerable<string> items, string? description = null)
    {
        var parts = new List<string>();
        foreach (var id in items)
        {
            // The list's own words for each, so the two read alike ("Caption: …", "Place taken …"), except the
            // description, read without "Description:" so it's what's heard first.
            var value = id switch
            {
                Position => $"{number} of {count}",
                Description => description?.Trim() ?? "",
                _ => row.SpokenText(id),
            };
            if (value.Length > 0) parts.Add(value);
        }
        return parts.Count > 0 ? Join(parts) : row.Name;
    }

    /// <summary>The parts with ", " between them, or just a space after one that ends a sentence (a description), so it
    /// reads "A dog on the beach. 12 of 400" rather than "beach., 12 of 400", as on the Mac.</summary>
    private static string Join(List<string> parts)
    {
        var text = new System.Text.StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1))
            text.Append(text[^1] is '.' or '!' or '?' ? " " : ", ").Append(part);
        return text.ToString();
    }
}
