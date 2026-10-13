using System.Text.Json;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

/// <summary>Properties take a trip through the file's property handler each, so they're saved to disk
/// after the first read; after that every list opens with its details already filled in. An entry is
/// used only while the file's size and modified time are unchanged.</summary>
public sealed class MetadataCache
{
    private sealed record Entry(long Size, long ModifiedTicks, MediaMetadata Metadata);

    private Dictionary<string, Entry>? _loaded;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly string _filePath;
    private int _unsaved;

    /// <summary>The file isn't read until first used, which is in the background scan: for a big library
    /// it's tens of megabytes, too slow to read before the window appears.</summary>
    public MetadataCache(string filePath) => _filePath = filePath;

    private Dictionary<string, Entry> Entries => _loaded ??= Load(_filePath);

    /// <summary>Entries are keyed by the file's UTC modified time, so changing time zone or a daylight
    /// saving change doesn't throw the whole cache away.</summary>
    private static long Stamp(MediaItem item) => item.Modified.ToUniversalTime().Ticks;

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "Metadata.json");

    public int Count { get { lock (_lock) return Entries.Count; } }

    public MediaMetadata? Lookup(MediaItem item)
    {
        lock (_lock)
        {
            return Entries.TryGetValue(item.Path, out var e) && e.Size == item.Size && e.ModifiedTicks == Stamp(item)
                   // Read by an older version without dc:description (or, for HEIC, before its XMP was read): read
                   // again, once. Not online-only files, which aren't read until they're downloaded: their older
                   // details are better than none.
                   && (e.Metadata.Version >= MediaMetadata.VersionNeeded(item.Path) || !XmpFields.CanWrite(item.Path) || item.IsOnlineOnly)
                ? e.Metadata : null;
        }
    }

    public void Remember(MediaItem item, MediaMetadata metadata)
    {
        lock (_lock)
        {
            Entries[item.Path] = new Entry(item.Size, Stamp(item), metadata);
            _unsaved++;
        }
    }

    /// <summary>Drops entries for files that are gone, so the cache doesn't grow forever.</summary>
    public void KeepOnly(IEnumerable<MediaItem> items)
    {
        var keep = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var path in Entries.Keys.Where(p => !keep.Contains(p)).ToList())
            {
                Entries.Remove(path);
                _unsaved++;
            }
        }
    }

    /// <summary>Writes the cache to disk. Safe to call from any thread; saves never overlap.</summary>
    public void Save()
    {
        lock (_saveLock) SaveNow();
    }

    private void SaveNow()
    {
        Dictionary<string, Entry> snapshot;
        lock (_lock)
        {
            if (_unsaved == 0) return;
            snapshot = new Dictionary<string, Entry>(Entries, StringComparer.OrdinalIgnoreCase);
            _unsaved = 0;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, snapshot);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves time; the next run reads the properties again.
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(stream);
                if (saved is not null) return new Dictionary<string, Entry>(saved, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged cache is just rebuilt.
        }
        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }
}

public static class AppPaths
{
    public static string DataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataFolderName);
}
