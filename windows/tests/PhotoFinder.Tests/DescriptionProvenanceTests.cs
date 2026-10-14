using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;

namespace PhotoFinder.Tests;

/// <summary>Each saved description says where it came from (#199), in the Mac's format and field names (#201), so an export
/// reads both apps' the same way.</summary>
[Collection(SharedState.Name)]
public sealed class DescriptionProvenanceTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];
    private DateTime _now = new(2026, 10, 14, 18, 30, 5, 250, DateTimeKind.Utc);

    public DescriptionProvenanceTests()
    {
        Announcer.Observer = _announced.Add;
        DescriptionProvenance.ClockOverride = () => _now;
        _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2024, 1, 2) });
    }

    public void Dispose()
    {
        DescriptionProvenance.ClockOverride = null;
        Announcer.Observer = null;
        _lib.Dispose();
    }

    private string File_ => _lib.PathOf(@"data\Descriptions.json");

    private static readonly MediaItem Item = new(@"C:\p\a.jpg", 10, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));

    private static string KeyOf(MediaItem item, string kind) =>
        $"{item.Path.ToUpperInvariant()}|{item.Size}|{item.Modified.ToUniversalTime().Ticks}|{kind}";

    private JsonObject Saved(DescriptionStore store)
    {
        store.Save();
        return JsonNode.Parse(File.ReadAllText(File_))!.AsObject();
    }

    [Fact]
    public void ADescriptionWindowsMade_IsSavedWithWhereItCameFrom_InTheMacsFormat()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "accessible", "A brown dog on a beach.", took: TimeSpan.FromMilliseconds(3120.4));

        var file = Saved(store);
        Assert.Equal(2, file["version"]!.GetValue<int>());
        var record = file["descriptions"]![KeyOf(Item, "accessible")]!.AsObject();
        // Exactly these fields, in the Mac's names; nothing unknown written as null.
        Assert.Equal(["text", "source", "prompt", "model", "locale", "os", "app", "created", "durationMs"],
            record.Select(p => p.Key).ToArray());
        Assert.Equal("A brown dog on a beach.", record["text"]!.GetValue<string>());
        Assert.Equal("windows-ai", record["source"]!.GetValue<string>());
        Assert.Equal("accessible", record["prompt"]!.GetValue<string>());
        Assert.Equal(DescriptionProvenance.Model, record["model"]!.GetValue<string>());
        Assert.StartsWith("Windows AI image description", record["model"]!.GetValue<string>());
        Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.Name, record["locale"]!.GetValue<string>());
        Assert.StartsWith("Windows", record["os"]!.GetValue<string>());
        Assert.Equal(Updates.AppVersion, record["app"]!.GetValue<string>());
        // ISO 8601 UTC to the second, as the Mac writes it.
        Assert.Equal("2026-10-14T18:30:05Z", record["created"]!.GetValue<string>());
        Assert.Equal(3120, record["durationMs"]!.GetValue<long>());

        var again = new DescriptionStore(File_).Record(Item, "accessible")!;
        Assert.Equal(store.Record(Item, "accessible"), again);
    }

    [Fact]
    public void ACorrection_KeepsHowItWasDescribed_AndTheAIsFirstText_ThroughLaterCorrections()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "accessible", "A brown dog stands near the water.", took: TimeSpan.FromSeconds(2));
        var made = store.Record(Item, "accessible")!;

        _now = _now.AddMinutes(4);
        store.Put(Item, "accessible", "Rex, our dog, at the lake.", edited: true);
        var first = store.Record(Item, "accessible")!;
        Assert.Equal("person", first.Source);
        Assert.True(store.IsEdited(Item, "accessible"));
        Assert.Equal("A brown dog stands near the water.", first.Original);
        Assert.Equal("2026-10-14T18:34:05Z", first.Edited);
        Assert.Equal((made.Prompt, made.Model, made.Locale, made.Os, made.App, made.Created, made.DurationMs),
            (first.Prompt, first.Model, first.Locale, first.Os, first.App, first.Created, first.DurationMs));

        _now = _now.AddMinutes(1);
        store.Put(Item, "accessible", "Rex at Lake Michigan.", edited: true);
        var second = store.Record(Item, "accessible")!;
        Assert.Equal("Rex at Lake Michigan.", second.Text);
        Assert.Equal("A brown dog stands near the water.", second.Original);
        Assert.Equal("2026-10-14T18:35:05Z", second.Edited);

        var record = Saved(store)["descriptions"]![KeyOf(Item, "accessible")]!.AsObject();
        Assert.Equal(["text", "source", "prompt", "model", "locale", "os", "app", "created", "durationMs", "edited", "original"],
            record.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void ThePersonsOwnDescription_WithNoneBefore_SaysWhenAndWhereOnly()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "accessible", "My own words.", edited: true);
        var record = Saved(store)["descriptions"]![KeyOf(Item, "accessible")]!.AsObject();
        Assert.Equal(["text", "source", "os", "app", "created", "edited"], record.Select(p => p.Key).ToArray());
        Assert.Equal("person", record["source"]!.GetValue<string>());
        Assert.Equal(record["created"]!.GetValue<string>(), record["edited"]!.GetValue<string>());
    }

    [Fact]
    public void Redescribe_ReplacesTheRecordWhole()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "accessible", "First.", took: TimeSpan.FromSeconds(1));
        store.Put(Item, "accessible", "Mine.", edited: true);
        _now = _now.AddHours(1);
        Assert.True(store.PutIfUnchanged(Item, "accessible", "Mine.", "Windows, asked again.", TimeSpan.FromSeconds(2)));
        var record = store.Record(Item, "accessible")!;
        Assert.Equal("windows-ai", record.Source);
        Assert.Null(record.Original);
        Assert.Null(record.Edited);
        Assert.Equal("2026-10-14T19:30:05Z", record.Created);
        Assert.Equal(2000, record.DurationMs);
    }

    [Fact]
    public void SavingTheSameTextAgain_WithoutDescribing_KeepsTheRecord()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "accessible", "A cat.", took: TimeSpan.FromSeconds(1));
        var made = store.Record(Item, "accessible");
        _now = _now.AddDays(1);
        // Describe Photo on a photo already described reads it back and saves it again.
        Assert.True(store.PutUnlessEdited(Item, "accessible", "A cat."));
        Assert.Equal(made, store.Record(Item, "accessible"));
    }

    [Fact]
    public void ChangingTheFile_MovesTheWholeRecord()
    {
        var store = new DescriptionStore(File_);
        store.Put(Item, "brief", "A cat.", took: TimeSpan.FromSeconds(1));
        store.Put(Item, "brief", "Our cat.", edited: true);
        var before = store.Record(Item, "brief");
        var now = new MediaItem(Item.Path, 20, new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        store.Rekey(Item.Path, Item.Size, Item.Modified, now);
        Assert.Equal(before, store.Record(now, "brief"));
        Assert.True(store.IsEdited(now, "brief"));
    }

    [Fact]
    public void TheFirstFormat_IsStillRead_ItsCorrectionsKept_AndWrittenInTheNewForm()
    {
        Directory.CreateDirectory(_lib.PathOf("data"));
        var made = KeyOf(Item, "accessible");
        var corrected = KeyOf(Item, "brief");
        File.WriteAllText(File_, JsonSerializer.Serialize(new Dictionary<string, string> { [made] = "From Windows.", [corrected] = "Mine." }));
        var editedFile = _lib.PathOf(@"data\EditedDescriptions.json");
        File.WriteAllText(editedFile, JsonSerializer.Serialize(new[] { corrected }));

        var store = new DescriptionStore(File_);
        Assert.Equal("From Windows.", store.Get(Item, "accessible"));
        Assert.False(store.IsEdited(Item, "accessible"));
        Assert.True(store.IsEdited(Item, "brief"));
        // Its details aren't known, so they're left out.
        Assert.Equal(new DescriptionRecord { Text = "From Windows." }, store.Record(Item, "accessible"));

        var file = Saved(store);
        Assert.Equal(2, file["version"]!.GetValue<int>());
        Assert.Equal(["text"], file["descriptions"]![made]!.AsObject().Select(p => p.Key).ToArray());
        Assert.False(File.Exists(editedFile));
        Assert.True(new DescriptionStore(File_).IsEdited(Item, "brief"));
    }

    [Fact]
    public void ARecordWithFieldsThisAppDoesntKnow_IsRead()
    {
        Directory.CreateDirectory(_lib.PathOf("data"));
        var key = KeyOf(Item, "accessible");
        var record = JsonNode.Parse("""
            {"text": "A dog.", "source": "apple-intelligence", "prompt": "Describe this photo.", "model": "AFM 3 Core",
             "locale": "en_US", "os": "macOS 27.2 (26B5101f)", "app": "0.2.0 (12)", "created": "2026-10-14T18:30:05Z",
             "durationMs": 3120, "somethingNew": {"a": 1}}
            """);
        var file = new JsonObject { ["version"] = 2, ["descriptions"] = new JsonObject { [key] = record }, ["alsoNew"] = true };
        File.WriteAllText(File_, file.ToJsonString());
        var read = new DescriptionStore(File_).Record(Item, "accessible")!;
        Assert.Equal("A dog.", read.Text);
        Assert.Equal("AFM 3 Core", read.Model);
        Assert.Equal(3120, read.DurationMs);
        Assert.False(read.IsEdited);
    }

    private string[] SetAside() => Directory.GetFiles(_lib.PathOf("data"), "Descriptions.unreadable-*.json");

    [Theory]
    [InlineData("{\"version\": 2, \"descriptions\": ")]
    [InlineData("[\"not\", \"an\", \"object\"]")]
    public void AFileThatCantBeRead_IsMovedAside_NotWrittenOver(string damaged)
    {
        Directory.CreateDirectory(_lib.PathOf("data"));
        File.WriteAllText(File_, damaged);
        var store = new DescriptionStore(File_);
        Assert.Null(store.Get(Item, "accessible"));
        Assert.Equal(damaged, File.ReadAllText(Assert.Single(SetAside())));
        // Saving starts a new file; the old one stays where it was put.
        store.Put(Item, "accessible", "A cat.");
        Assert.Equal("A cat.", Saved(store)["descriptions"]![KeyOf(Item, "accessible")]!["text"]!.GetValue<string>());
        Assert.Equal(damaged, File.ReadAllText(Assert.Single(SetAside())));
    }

    [Fact]
    public void ARecordThatCantBeRead_IsSkipped_TheOthersKept_AndTheFileKeptAside()
    {
        Directory.CreateDirectory(_lib.PathOf("data"));
        var good = KeyOf(Item, "accessible");
        var mixed = KeyOf(Item, "brief");
        var bad = KeyOf(Item, "detailed");
        var file = new JsonObject
        {
            ["version"] = 3,
            ["descriptions"] = new JsonObject
            {
                [good] = new JsonObject { ["text"] = "A dog.", ["source"] = "windows-ai", ["durationMs"] = 3120 },
                [mixed] = "A plain one.",
                [bad] = new JsonObject { ["text"] = "Odd.", ["durationMs"] = 3.5 },
                ["empty"] = new JsonObject { ["text"] = "" },
                ["number"] = 12,
            },
        };
        File.WriteAllText(File_, file.ToJsonString());
        var store = new DescriptionStore(File_);
        Assert.Equal("A dog.", store.Get(Item, "accessible"));
        Assert.Equal("A plain one.", store.Get(Item, "brief"));
        Assert.Null(store.Get(Item, "detailed"));
        Assert.Equal(file.ToJsonString(), File.ReadAllText(Assert.Single(SetAside())));
        Assert.True(File.Exists(File_));
    }

    [WpfFact]
    public async Task DescribingAsYouArrow_SavesHowLongItTook()
    {
        var store = new DescriptionStore(File_);
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1)) { Descriptions = store };
        model.SelectionRequested += (ids, _) => model.SelectedRows = model.Rows.Where(r => ids.Contains(r.Id)).ToList();
        await model.StartAsync();
        await model.DetailFillTask;
        var ready = new AIReadiness(true, "Ready", null);
        var auto = new AutoDescriber(model, store,
            async (item, _, _) => { await Task.Delay(10); return new Described($"A picture called {item.Name}.", TimeSpan.FromMilliseconds(1234.4)); },
            _ => Task.FromResult(ready), _ => Task.FromResult(ready), TimeSpan.FromMilliseconds(30));
        using (model) using (auto)
        {
            model.SelectedRows = model.Rows.Where(r => r.Name == "a.jpg").ToList();
            await auto.WhenIdleAsync();
            var record = store.Record(model.FocusedRow!.Item!, "accessible")!;
            Assert.Equal("windows-ai", record.Source);
            Assert.Equal("accessible", record.Prompt);
            // Windows' own time for the describing, not the wait around it.
            Assert.Equal(1234, record.DurationMs);

            // Describe Photo on it again reads the saved one: the record isn't remade.
            _now = _now.AddDays(1);
            await auto.DescribeNowAsync(model.FocusedRow!, () => true);
            Assert.Equal(record, store.Record(model.FocusedRow!.Item!, "accessible"));
        }
    }
}
