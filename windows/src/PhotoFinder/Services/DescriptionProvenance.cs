using System.Globalization;
using System.Text.Json.Serialization;

namespace PhotoFinder.Services;

/// <summary>
/// One saved description and where it came from (#199, Kelly, 14 Oct), in Descriptions.json. The field names and their
/// meanings are the Mac's (DescriptionCache.swift), so an export (to Image Description Toolkit, #200) reads both apps' the
/// same way. A field that isn't known is left out, never written as null; readers ignore fields they don't know.
/// </summary>
public sealed record DescriptionRecord
{
    /// <summary>The description. Always present.</summary>
    public string Text { get; init; } = "";

    /// <summary>"windows-ai", "apple-intelligence", or "person" once the person wrote or corrected it: a "person" record is
    /// the "Edited by you" mark.</summary>
    public string? Source { get; init; }

    /// <summary>On Windows, the Description Type asked for ("accessible", "brief"…); on the Mac, the prompt's text.</summary>
    public string? Prompt { get; init; }

    /// <summary>The model that described it, as far as Windows says. Windows names no model and lists no package for it,
    /// so this is "Windows AI image description" and the version of Windows' AI components (MicrosoftWindows.Client.CoreAI),
    /// which follows Windows' own build.</summary>
    public string? Model { get; init; }

    /// <summary>The language the app was in when it was described ("en-US").</summary>
    public string? Locale { get; init; }

    /// <summary>"Windows 11 Pro 26H2, build 26340.9616".</summary>
    public string? Os { get; init; }

    /// <summary>The app's version ("0.2.0.1180").</summary>
    public string? App { get; init; }

    /// <summary>When it was described (or, for the person's own, written): ISO 8601 UTC to the second.</summary>
    public string? Created { get; init; }

    /// <summary>About how long describing took, in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>When the person last corrected it, ISO 8601 UTC. Only on corrections.</summary>
    public string? Edited { get; init; }

    /// <summary>The AI's text before the first correction, kept through later ones. Only on corrections.</summary>
    public string? Original { get; init; }

    [JsonIgnore]
    public bool IsEdited => Source == DescriptionProvenance.Person;
}

/// <summary>A description Windows made, and how long it took over it (not the wait for the helper), when that's known.
/// A plain string converts to one, so stand-ins in tests can return text.</summary>
public readonly record struct Described(string Text, TimeSpan? Took)
{
    public static implicit operator Described(string text) => new(text, null);
}

/// <summary>What's recorded about this PC and app with each description. Each is read once; the model's details come
/// from Windows' package list, which is slow, so <see cref="Warm"/> reads them at startup, off the window's thread.</summary>
public static class DescriptionProvenance
{
    public const string WindowsAI = "windows-ai";
    public const string Person = "person";

    private static readonly Lazy<string?> _os = new(SystemDetails.WindowsEdition);
    private static readonly Lazy<string> _app = new(() => Updates.AppVersion);
    private static readonly Lazy<string?> _model = new(ReadModel);

    /// <summary>Stand-ins for tests; null reads this PC.</summary>
    internal static string? OsOverride { get; set; }
    internal static string? ModelOverride { get; set; }
    internal static Func<DateTime>? ClockOverride { get; set; }

    public static string? Os => OsOverride ?? _os.Value;
    public static string App => _app.Value;
    public static string? Model => ModelOverride ?? _model.Value;
    public static string Locale => CultureInfo.CurrentUICulture.Name;
    public static DateTime Now => ClockOverride?.Invoke() ?? DateTime.UtcNow;

    /// <summary>ISO 8601 UTC to the second, as the Mac writes it: "2026-10-14T18:30:05Z".</summary>
    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static void Warm() => Task.Run(() => { _ = _os.Value; _ = _model.Value; });

    /// <summary>A description Windows AI just made.</summary>
    public static DescriptionRecord Made(string text, string kind, TimeSpan? took) => new()
    {
        Text = text,
        Source = WindowsAI,
        Prompt = kind,
        Model = Model,
        Locale = Locale,
        Os = Os,
        App = App,
        Created = Stamp(Now),
        DurationMs = took is { } t ? (long)Math.Round(t.TotalMilliseconds) : null,
    };

    /// <summary>The person's correction of <paramref name="before"/> (or their own description, when there was none): how
    /// it was described and the AI's first text are kept. <paramref name="now"/> is a record <see cref="Made"/> for the new
    /// text, for this PC's details and the time.</summary>
    public static DescriptionRecord Corrected(DescriptionRecord? before, DescriptionRecord now)
    {
        if (before is null)
            return new() { Text = now.Text, Source = Person, Os = now.Os, App = now.App, Created = now.Created, Edited = now.Created };
        return before with
        {
            Text = now.Text,
            Source = Person,
            Edited = now.Created,
            Original = before.IsEdited ? before.Original : before.Text,
        };
    }

    /// <summary>"Windows AI image description, MicrosoftWindows.Client.CoreAI 1000.26100.9616.0": Windows names no model
    /// and lists no package for the image description one, so its AI components' version is the nearest thing.</summary>
    private static string? ReadModel()
    {
        const string name = "Windows AI image description";
        var core = SystemDetails.AIPackages().FirstOrDefault(p => p.Name == "MicrosoftWindows.Client.CoreAI");
        return core.Name is null ? name : $"{name}, {core.Name} {core.Version}";
    }
}
