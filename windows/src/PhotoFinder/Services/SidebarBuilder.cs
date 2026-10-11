using System.Globalization;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

public static class SidebarIds
{
    public const string LibrarySection = "section:library";
    public const string DatesSection = "section:dates";
    public const string FoldersSection = "section:folders";
    public const string AllPhotos = "library:all";

    public static string Year(int year) => $"year:{year}";
    public static string Month(int year, int month) => $"month:{year}-{month}";
    public static string Folder(string path) => "folder:" + path.ToUpperInvariant();
}

/// <summary>Builds the sidebar from the library: Library (All Photos and the smart albums), Dates (a folder
/// for each year, with a folder for each month inside) and Folders (the library's folders on disk).</summary>
public static class SidebarBuilder
{
    public static readonly TimeSpan RecentlyAddedWindow = TimeSpan.FromDays(30);

    private static readonly (SourceKind Kind, string Id, string Title)[] SmartAlbums =
    [
        (SourceKind.Favorites, "library:favorites", "Favorites"),
        (SourceKind.RecentlyAdded, "library:recent", "Recently Added"),
        (SourceKind.Videos, "library:videos", "Videos"),
        (SourceKind.Screenshots, "library:screenshots", "Screenshots"),
        (SourceKind.ScreenRecordings, "library:recordings", "Screen Recordings"),
        (SourceKind.Panoramas, "library:panoramas", "Panoramas"),
        (SourceKind.Raw, "library:raw", "RAW"),
        (SourceKind.Animated, "library:animated", "Animated"),
    ];

    /// <summary>Whether an item belongs in a smart album, year or month. Folders are matched by path instead.</summary>
    public static bool Matches(SidebarItem source, MediaItem item, DateTime now) => source.Kind switch
    {
        SourceKind.AllPhotos => true,
        SourceKind.Favorites => item.IsFavorite,
        SourceKind.Videos => item.IsVideo,
        SourceKind.Screenshots => MediaTypes.IsScreenshot(item),
        SourceKind.ScreenRecordings => MediaTypes.IsScreenRecording(item),
        SourceKind.RecentlyAdded => now - item.Created <= RecentlyAddedWindow && item.Created <= now.AddDays(1),
        SourceKind.Panoramas => MediaTypes.IsPanorama(item),
        SourceKind.Raw => MediaTypes.IsRaw(item.Path),
        SourceKind.Animated => MediaTypes.IsAnimated(item.Path),
        SourceKind.Year => item.Date.Year == source.Year,
        SourceKind.Month => item.Date.Year == source.Year && item.Date.Month == source.Month,
        SourceKind.Folder => string.Equals(item.Folder, source.FolderPath, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static List<SidebarItem> Build(IReadOnlyCollection<MediaItem> items, IReadOnlyList<string> roots, DateTime now)
    {
        var sections = new List<SidebarItem>();

        var library = new List<SidebarItem>
        {
            new() { Id = SidebarIds.AllPhotos, Title = "All Photos", Kind = SourceKind.AllPhotos, Count = items.Count,
                    ParentId = SidebarIds.LibrarySection, ContainerKind = "Library" },
        };
        foreach (var (kind, id, title) in SmartAlbums)
        {
            var probe = new SidebarItem { Id = id, Title = title, Kind = kind, ContainerKind = "Album" };
            var count = items.Count(i => Matches(probe, i, now));
            if (count > 0)
                library.Add(new() { Id = id, Title = title, Kind = kind, Count = count, ParentId = SidebarIds.LibrarySection, ContainerKind = "Album" });
        }
        sections.Add(Section(SidebarIds.LibrarySection, "Library", library));

        var years = items
            .GroupBy(i => i.Date.Year)
            .OrderByDescending(g => g.Key)
            .Select(year =>
            {
                var yearId = SidebarIds.Year(year.Key);
                var months = year.GroupBy(i => i.Date.Month).OrderByDescending(g => g.Key).Select(month => new SidebarItem
                {
                    Id = SidebarIds.Month(year.Key, month.Key),
                    Title = new DateTime(year.Key, month.Key, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                    Kind = SourceKind.Month, Year = year.Key, Month = month.Key, Count = month.Count(),
                    ParentId = yearId, ContainerKind = "Month",
                }).ToList();
                return new SidebarItem
                {
                    Id = yearId, Title = year.Key.ToString(CultureInfo.CurrentCulture), Kind = SourceKind.Year, Year = year.Key,
                    Count = year.Count(), Children = months, ParentId = SidebarIds.DatesSection, ContainerKind = "Year",
                };
            })
            .ToList();
        if (years.Count > 0) sections.Add(Section(SidebarIds.DatesSection, "Dates", years));

        var folders = BuildFolders(items, roots);
        if (folders.Count > 0) sections.Add(Section(SidebarIds.FoldersSection, "Folders", folders));

        return sections;
    }

    private static SidebarItem Section(string id, string title, List<SidebarItem> children) => new()
    {
        Id = id, Title = title, Kind = SourceKind.Section, Count = children.Count, Children = children, ContainerKind = "Section",
    };

    /// <summary>A folder tree for each root, holding only folders with photos or videos somewhere inside.
    /// A folder's count is everything in it and below it.</summary>
    private static List<SidebarItem> BuildFolders(IReadOnlyCollection<MediaItem> items, IReadOnlyList<string> roots)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var childFolders = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var root = roots.FirstOrDefault(r => item.Folder.Equals(r, StringComparison.OrdinalIgnoreCase) || LibraryScanner.IsInside(item.Folder, r));
            if (root is null) continue;
            var folder = item.Folder;
            while (true)
            {
                totals[folder] = totals.GetValueOrDefault(folder) + 1;
                if (folder.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                var parent = Path.GetDirectoryName(folder);
                if (parent is null) break;
                if (!childFolders.TryGetValue(parent, out var set))
                    childFolders[parent] = set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                set.Add(folder);
                folder = parent;
            }
        }

        SidebarItem Node(string path, string title, string parentId)
        {
            var id = SidebarIds.Folder(path);
            var children = childFolders.TryGetValue(path, out var set)
                ? set.Select(child => Node(child, Path.GetFileName(child), id)).ToList()
                : [];
            return new SidebarItem
            {
                Id = id, Title = title, Kind = SourceKind.Folder, FolderPath = path, Count = totals.GetValueOrDefault(path),
                Children = children, ParentId = parentId, ContainerKind = "Folder",
            };
        }

        var names = roots.Select(r => Path.GetFileName(r) is { Length: > 0 } n ? n : r).ToList();
        return roots
            .Select((root, i) => (root, title: names.Count(n => n.Equals(names[i], StringComparison.CurrentCultureIgnoreCase)) > 1 ? root : names[i]))
            .Where(r => totals.ContainsKey(r.root))
            .Select(r => Node(r.root, r.title, SidebarIds.FoldersSection))
            .ToList();
    }

    /// <summary>Every item in the tree, by id.</summary>
    public static Dictionary<string, SidebarItem> Index(IEnumerable<SidebarItem> sections)
    {
        var index = new Dictionary<string, SidebarItem>();
        void Add(IEnumerable<SidebarItem> list)
        {
            foreach (var item in list)
            {
                index[item.Id] = item;
                Add(item.Children);
            }
        }
        Add(sections);
        return index;
    }
}
