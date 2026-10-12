using System.Text.Json;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

/// <summary>Windows AI descriptions already made, saved to disk so a photo is described once, not every time
/// someone arrows to it. Keyed by file, size, modified time and description kind: an edited photo, or a
/// different kind, is described again.</summary>
public sealed class DescriptionStore
{
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private Dictionary<string, string>? _loaded;

    public DescriptionStore(string filePath) => _filePath = filePath;

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "Descriptions.json");

    private Dictionary<string, string> Entries => _loaded ??= Load(_filePath);

    private static string Key(MediaItem item, string kind) =>
        $"{item.Path.ToUpperInvariant()}|{item.Size}|{item.Modified.ToUniversalTime().Ticks}|{kind}";

    public string? Get(MediaItem item, string kind)
    {
        lock (_lock) return Entries.TryGetValue(Key(item, kind), out var text) ? text : null;
    }

    /// <summary>How many photos have a saved description, of any type.</summary>
    public int PhotoCount
    {
        get
        {
            lock (_lock) return Entries.Keys.Select(k => k[..Math.Max(0, k.LastIndexOf('|'))]).Distinct().Count();
        }
    }

    private bool _saveScheduled;

    public void Put(MediaItem item, string kind, string text)
    {
        lock (_lock) Entries[Key(item, kind)] = text;
        ScheduleSave();
    }

    /// <summary>Saves a couple of seconds after the last change, not after every one: background describing adds
    /// descriptions one after another, and rewriting the whole file each time adds up fast.</summary>
    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_saveScheduled) return;
            _saveScheduled = true;
        }
        Task.Delay(SaveDelay).ContinueWith(_ =>
        {
            lock (_lock) _saveScheduled = false;
            Save();
        });
    }

    /// <summary>How long after a change it's saved. Short in tests.</summary>
    internal TimeSpan SaveDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Keeps a file's descriptions when Photo Finder itself changes the file (a caption, rating or
    /// favorite), which changes its size and date but not the picture.</summary>
    public void Rekey(string path, long oldSize, DateTime oldModified, MediaItem now)
    {
        var oldPrefix = $"{path.ToUpperInvariant()}|{oldSize}|{oldModified.ToUniversalTime().Ticks}|";
        var moved = false;
        lock (_lock)
        {
            foreach (var (key, text) in Entries.Where(e => e.Key.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList())
            {
                Entries[Key(now, key[oldPrefix.Length..])] = text;
                moved = true;
            }
        }
        if (moved) ScheduleSave();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            Dictionary<string, string> snapshot;
            lock (_lock) snapshot = new Dictionary<string, string>(Entries);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Descriptions can be made again; not worth interrupting anyone for.
            }
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts over.
        }
        return [];
    }
}
