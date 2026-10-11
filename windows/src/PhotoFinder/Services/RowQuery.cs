using PhotoFinder.Models;

namespace PhotoFinder.Services;

/// <summary>What the list shows for a sidebar item, filtered by search and sorted by a column.</summary>
public static class RowQuery
{
    /// <summary>A section, year or folder lists its children as rows; a disk folder also lists the
    /// photos directly inside it; everything else lists its photos.</summary>
    public static List<FileRow> RowsFor(SidebarItem source, IEnumerable<MediaItem> items, DateTime now)
    {
        var rows = source.Children.Select(FileRow.ForContainer).ToList();
        if (source.Kind is SourceKind.Section or SourceKind.Year) return rows;
        rows.AddRange(items.Where(i => SidebarBuilder.Matches(source, i, now)).Select(FileRow.ForItem));
        return rows;
    }

    public static bool MatchesSearch(FileRow row, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        return Contains(row.Name, q) || Contains(row.Kind, q) || Contains(row.Caption, q)
            || Contains(row.Keywords, q) || Contains(row.DateText, q);
    }

    private static bool Contains(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Folders come first, as in File Explorer, then rows in column order. Ties are always A to Z,
    /// so folders without dates don't list backwards when the list is newest first.</summary>
    public static IComparer<FileRow> Comparer(string column, bool descending) => Comparer<FileRow>.Create((a, b) =>
    {
        if (a.IsContainer != b.IsContainer) return a.IsContainer ? -1 : 1;
        var result = CompareBy(column, a, b);
        if (result != 0) return descending ? -result : result;
        return column == Columns.Name ? 0 : NameOrder(a, b);
    });

    private static int NameOrder(FileRow a, FileRow b) => NaturalComparer.Instance.Compare(a.Name, b.Name);

    private static int CompareBy(string column, FileRow a, FileRow b) => column switch
    {
        Columns.Name => NameOrder(a, b),
        "date" => Nullable.Compare(a.Date, b.Date),
        "modified" => Nullable.Compare(a.Modified, b.Modified),
        "dimensions" => a.PixelCount.CompareTo(b.PixelCount),
        "megapixels" => a.Megapixels.CompareTo(b.Megapixels),
        "duration" => a.Duration.CompareTo(b.Duration),
        "favorite" => a.IsFavorite.CompareTo(b.IsFavorite),
        "rating" => a.Rating.CompareTo(b.Rating),
        "size" => a.Size.CompareTo(b.Size),
        _ => string.Compare(a.ColumnText(column), b.ColumnText(column), StringComparison.CurrentCultureIgnoreCase),
    };
}

/// <summary>Sorts "IMG_2" before "IMG_10", the way File Explorer does.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
