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

    private Dictionary<string, DescriptionRecord> Entries => _loaded ??= Load();

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
        // Made before the lock: what it reads about this PC may still be being read at startup.
        var made = DescriptionProvenance.Made(text, kind, took);
        lock (_lock)
        {
            var key = Key(item, kind);
            Entries.TryGetValue(key, out var before);
            if (edited) Entries[key] = DescriptionProvenance.Corrected(before, made);
            else if (took is not null || before is null || before.Text != text || before.IsEdited) Entries[key] = made;
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
        // Descriptions in any language are written as they read, not escaped: the file is the app's own, never in a page.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The file couldn't be read and couldn't be moved aside either: it's never written over, so nothing in it is
    /// lost, and descriptions made meanwhile are kept only until the app quits.</summary>
    private bool _keepFile;

    private sealed record FileContents(int Version, Dictionary<string, DescriptionRecord> Descriptions);

    public void Save()
    {
        lock (_saveLock)
        {
            Dictionary<string, DescriptionRecord> snapshot;
            lock (_lock) snapshot = new Dictionary<string, DescriptionRecord>(Entries);
            if (_keepFile) return;
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

    /// <summary>The saved descriptions. A record that can't be read is skipped, not the file; a file that can't be read
    /// at all is moved aside (Descriptions.unreadable-<i>date</i>.json, as on the Mac), and either way the file as it was is
    /// kept, so saving never loses what was in it. A file that can't be opened is left alone and never written.</summary>
    private Dictionary<string, DescriptionRecord> Load()
    {
        string json;
        try
        {
            if (!File.Exists(_filePath)) return [];
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("descriptions", "The saved descriptions couldn't be opened, so they're left as they are", ex);
            _keepFile = true;
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Not an object.");
            var loaded = new Dictionary<string, DescriptionRecord>();
            var skipped = 0;
            var v2 = root.TryGetProperty("descriptions", out var records) && records.ValueKind == JsonValueKind.Object;
            // The first format, from before 0.2.0, was {key: text}: its details aren't known, only which were corrections.
            var edited = v2 ? null : LoadEdited(EditedPath);
            foreach (var p in (v2 ? records : root).EnumerateObject())
            {
                var record = Read(p.Value);
                if (record is null) { skipped++; continue; }
                if (edited?.Contains(p.Name) == true) record = record with { Source = DescriptionProvenance.Person };
                loaded[p.Name] = record;
            }
            if (skipped > 0)
            {
                Log.Write("descriptions", $"{skipped} saved description(s) couldn't be read and were skipped");
                SetAside(copy: true);
            }
            return loaded;
        }
        catch (JsonException ex)
        {
            Log.Error("descriptions", "The saved descriptions couldn't be read", ex);
            SetAside(copy: false);
            return [];
        }
    }

    /// <summary>One record, or a plain string (the first format's); null when it's neither, or has no text.</summary>
    private static DescriptionRecord? Read(JsonElement value)
    {
        try
        {
            var record = value.ValueKind switch
            {
                JsonValueKind.String => new DescriptionRecord { Text = value.GetString()! },
                JsonValueKind.Object => value.Deserialize<DescriptionRecord>(Json),
                _ => null,
            };
            return record is { Text.Length: > 0 } ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Keeps the file as it was beside it, before anything is saved over it. If that fails, nothing is saved.</summary>
    private void SetAside(bool copy)
    {
        var aside = Path.Combine(Path.GetDirectoryName(_filePath)!,
            $"{Path.GetFileNameWithoutExtension(_filePath)}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            if (copy) File.Copy(_filePath, aside, overwrite: true);
            else File.Move(_filePath, aside, overwrite: true);
            Log.Write("descriptions", $"The saved descriptions were kept as {Path.GetFileName(aside)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("descriptions", "The saved descriptions couldn't be kept aside, so they're left as they are", ex);
            _keepFile = true;
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
}
