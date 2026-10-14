using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoFinder.Models;

namespace PhotoFinder.Services;

/// <summary>Windows AI descriptions already made, saved to disk so a photo is described once, not every time
/// someone arrows to it. Keyed by file, size, modified time and description kind: an edited photo, or a
/// different kind, is described again. Each is saved with where it came from (#199): see <see cref="DescriptionRecord"/>.
/// The file is <c>{"version": 2, "descriptions": {key: record}}</c>, as on the Mac.</summary>
public sealed class DescriptionStore
{
    /// <summary>The file's format: 2 has a record for each description; 1 (no version) was a plain {key: text} map, with
    /// the corrected keys in EditedDescriptions.json beside it.</summary>
    public const int FormatVersion = 2;

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private Dictionary<string, DescriptionRecord>? _loaded;

    public DescriptionStore(string filePath) => _filePath = filePath;

    /// <summary>Where the first format kept the corrected keys. Read with an old file, and removed once the new one is
    /// written, since the records say which are corrections now.</summary>
    private string EditedPath => Path.Combine(Path.GetDirectoryName(_filePath)!, "EditedDescriptions.json");

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "Descriptions.json");

    private Dictionary<string, DescriptionRecord> Entries => _loaded ??= Load(_filePath, EditedPath);

    private static string Key(MediaItem item, string kind) =>
        $"{item.Path.ToUpperInvariant()}|{item.Size}|{item.Modified.ToUniversalTime().Ticks}|{kind}";

    public string? Get(MediaItem item, string kind) => Record(item, kind)?.Text;

    /// <summary>The saved description and where it came from, or null.</summary>
    public DescriptionRecord? Record(MediaItem item, string kind)
    {
        lock (_lock) return Entries.TryGetValue(Key(item, kind), out var record) ? record : null;
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

    /// <param name="edited">The person wrote or corrected it (the Description panel): saved as theirs, keeping how it was
    /// described and the AI's first text. Otherwise it's a description Windows made, replacing the record whole.</param>
    /// <param name="took">How long Windows took to make it. Without it, saving the same text as is already saved (a
    /// description read back from here) keeps the record as it was.</param>
    public void Put(MediaItem item, string kind, string text, bool edited = false, TimeSpan? took = null)
    {
        lock (_lock)
        {
            var key = Key(item, kind);
            Entries.TryGetValue(key, out var before);
            if (edited) Entries[key] = DescriptionProvenance.Corrected(before, text);
            else if (took is not null || before is null || before.Text != text || before.IsEdited)
                Entries[key] = DescriptionProvenance.Made(text, kind, took);
        }
        ScheduleSave();
        Saved?.Invoke(item, kind, text);
    }

    /// <summary>Saves a description Windows made, unless the person has corrected this one meanwhile: only Redescribe,
    /// after asking, replaces a correction. Returns whether it was saved.</summary>
    public bool PutUnlessEdited(MediaItem item, string kind, string text, TimeSpan? took = null)
    {
        if (IsEdited(item, kind)) return false;
        Put(item, kind, text, took: took);
        return true;
    }

    /// <summary>Redescribe on a correction, agreed to when the saved description was <paramref name="expected"/>: the
    /// new description replaces it only if nobody has corrected it again since. Returns whether it was saved.</summary>
    public bool PutIfUnchanged(MediaItem item, string kind, string expected, string text, TimeSpan? took = null)
    {
        if (Get(item, kind) != expected && IsEdited(item, kind)) return false;
        Put(item, kind, text, took: took);
        return true;
    }

    public bool IsEdited(MediaItem item, string kind) => Record(item, kind)?.IsEdited == true;

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

    /// <summary>Keeps a file's descriptions when My Photo Library Described itself changes the file (a caption, rating or
    /// favorite), which changes its size and date but not the picture.</summary>
    public void Rekey(string path, long oldSize, DateTime oldModified, MediaItem now)
    {
        var oldPrefix = $"{path.ToUpperInvariant()}|{oldSize}|{oldModified.ToUniversalTime().Ticks}|";
        var moved = false;
        lock (_lock)
        {
            foreach (var (key, record) in Entries.Where(e => e.Key.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList())
            {
                Entries[Key(now, key[oldPrefix.Length..])] = record;
                moved = true;
            }
        }
        if (moved) ScheduleSave();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record FileContents(int Version, Dictionary<string, DescriptionRecord> Descriptions);

    public void Save()
    {
        lock (_saveLock)
        {
            Dictionary<string, DescriptionRecord> snapshot;
            lock (_lock) snapshot = new Dictionary<string, DescriptionRecord>(Entries);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new FileContents(FormatVersion, snapshot), Json));
                File.Move(temp, _filePath, overwrite: true);
                File.Delete(EditedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Descriptions can be made again; not worth interrupting anyone for.
            }
        }
    }

    private static Dictionary<string, DescriptionRecord> Load(string path, string editedPath)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (root.TryGetProperty("descriptions", out var records) && records.ValueKind == JsonValueKind.Object)
                return records.Deserialize<Dictionary<string, DescriptionRecord>>(Json)?
                    .Where(e => e.Value is { Text.Length: > 0 }).ToDictionary() ?? [];
            // The first format, from before 0.2.0: its details aren't known, only which were corrections.
            var edited = LoadEdited(editedPath);
            return root.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => new DescriptionRecord
                {
                    Text = p.Value.GetString()!,
                    Source = edited.Contains(p.Name) ? DescriptionProvenance.Person : null,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts over.
        }
        return [];
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
}
