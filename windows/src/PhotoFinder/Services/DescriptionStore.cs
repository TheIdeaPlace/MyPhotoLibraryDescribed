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
    private HashSet<string>? _editedLoaded;

    public DescriptionStore(string filePath) => _filePath = filePath;

    /// <summary>The descriptions the person wrote or corrected in the Description panel (#50), by key. In their own file
    /// beside Descriptions.json, as on the Mac, so the descriptions file's format doesn't change.</summary>
    private HashSet<string> Edited => _editedLoaded ??= LoadEdited(EditedPath);

    private string EditedPath => Path.Combine(Path.GetDirectoryName(_filePath)!, "EditedDescriptions.json");

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

    /// <param name="edited">The person wrote or corrected it (the Description panel); a description Windows made clears
    /// the mark.</param>
    public void Put(MediaItem item, string kind, string text, bool edited = false)
    {
        lock (_lock)
        {
            var key = Key(item, kind);
            Entries[key] = text;
            if (edited) Edited.Add(key);
            else Edited.Remove(key);
        }
        ScheduleSave();
        Saved?.Invoke(item, kind, text);
    }

    /// <summary>Saves a description Windows made, unless the person has corrected this one meanwhile: only Describe Again,
    /// after asking, replaces a correction. Returns whether it was saved.</summary>
    public bool PutUnlessEdited(MediaItem item, string kind, string text)
    {
        if (IsEdited(item, kind)) return false;
        Put(item, kind, text);
        return true;
    }

    public bool IsEdited(MediaItem item, string kind)
    {
        lock (_lock) return Edited.Contains(Key(item, kind));
    }

    /// <summary>A description was saved, however it was made: Save Descriptions as Captions writes it to the caption.</summary>
    public event Action<MediaItem, string, string>? Saved;

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
                var newKey = Key(now, key[oldPrefix.Length..]);
                Entries[newKey] = text;
                if (Edited.Contains(key)) Edited.Add(newKey);
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
            List<string> edited;
            lock (_lock)
            {
                snapshot = new Dictionary<string, string>(Entries);
                edited = [.. Edited.Where(snapshot.ContainsKey).Order(StringComparer.Ordinal)];
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                File.Move(temp, _filePath, overwrite: true);
                var editedTemp = EditedPath + ".tmp";
                File.WriteAllText(editedTemp, JsonSerializer.Serialize(edited));
                File.Move(editedTemp, EditedPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Descriptions can be made again; not worth interrupting anyone for.
            }
        }
    }

    private static HashSet<string> LoadEdited(string path)
    {
        try
        {
            if (File.Exists(path))
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file: corrections lose their mark, not their text.
        }
        return new HashSet<string>(StringComparer.Ordinal);
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
