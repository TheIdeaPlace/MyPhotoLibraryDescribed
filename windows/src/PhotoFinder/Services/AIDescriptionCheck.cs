using System.Diagnostics;
using System.Globalization;

namespace PhotoFinder.Services;

/// <summary>
/// Help > Check AI Description (#95): describes a sample picture that ships with the app, to show describing works end to
/// end, apart from the person's own photos, with a known result for bug reports. The sample is drawn by
/// scripts\MakeSamplePhoto.cs: a red mug on a wooden table beside a sign reading OPEN. Nothing is changed or saved: no
/// photo is touched and the description isn't kept.
/// </summary>
public sealed class AIDescriptionCheck : ICheckResult
{
    /// <summary>What the sample shows, for the person and for a bug report.</summary>
    public const string SampleShows = "A red mug on a wooden table beside a sign reading OPEN";

    /// <summary>Things a description of the sample should mention, each with the words that count. Only a hint: wording
    /// varies, so a miss is said, never counted as a failure.</summary>
    internal static readonly (string Thing, string[] Words)[] Expected =
    [
        ("the mug", ["mug", "cup"]),
        // Apple Intelligence calls the table "a wooden surface" (#13, 13 Oct).
        ("the table", ["table", "tabletop", "desk", "surface", "counter", "countertop"]),
        ("the OPEN sign", ["open"]),
    ];

    public delegate Task<string> DescribeFunc(Func<uint, Task<byte[]>> jpegAtSize, string kind, CancellationToken cancel);

    /// <summary>The sample, as the JPEG bytes that go to Windows AI.</summary>
    public static byte[] SampleJpeg()
    {
        using var stream = typeof(AIDescriptionCheck).Assembly.GetManifestResourceStream("SamplePhoto.jpg")
            ?? throw new InvalidOperationException("The sample photo is missing from the app.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Describes the sample with the description type in use. Failures are the result, not exceptions; only
    /// cancelling (closing the window) throws.</summary>
    public static async Task<AIDescriptionCheck> RunAsync(DescribeFunc describe, string kind, AIReadiness? readiness, CancellationToken cancel)
    {
        // Windows AI can't describe here, or would first download its model (minutes, under "Describing the sample
        // photo"): say so rather than try.
        if (readiness is { Usable: false })
            return new AIDescriptionCheck(kind, readiness, TimeSpan.Zero, null, readiness.State,
                readiness.Message ?? WindowsAI.MessageForState(readiness.State) ?? $"Windows AI isn't available ({readiness.State}).");
        if (readiness is { State: "NotReady" })
            return new AIDescriptionCheck(kind, readiness, TimeSpan.Zero, null, readiness.State,
                "Windows' image description model isn't downloaded yet. Describe Photo (Ctrl+D) downloads it.");
        var jpeg = SampleJpeg();
        var clock = Stopwatch.StartNew();
        try
        {
            // The sample is small already; Windows AI scales it, so every size asked for gets the same picture.
            var text = await describe(_ => Task.FromResult(jpeg), kind, cancel);
            return new AIDescriptionCheck(kind, readiness, clock.Elapsed, text, null, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (DescriptionException ex)
        {
            return new AIDescriptionCheck(kind, readiness, clock.Elapsed, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // Not one of the helper's answers: its message may name a folder (the helper's path, in the user's folder),
            // so a bug report gets only its type.
            Log.Error("describe", "Check AI Description failed", ex);
            return new AIDescriptionCheck(kind, readiness, clock.Elapsed, null, ex.GetType().Name, ex.Message, messageIsPrivate: true);
        }
    }

    public AIDescriptionCheck(string kind, AIReadiness? readiness, TimeSpan took, string? description, string? code, string? message,
        bool messageIsPrivate = false)
    {
        MessageIsPrivate = messageIsPrivate;
        Kind = kind;
        Took = took;
        Description = description;
        Code = code;
        Message = message;
        Missing = description is null ? [] : Expected.Where(e => !e.Words.Any(w => Mentions(description, w))).Select(e => e.Thing).ToList();

        var items = new List<SystemCheck.Item>();
        if (Worked)
        {
            // First, so it's what focus lands on and what's read.
            items.Add(new("Description", SystemCheck.Status.Info, description!.Length > 0 ? description : "Windows AI returned an empty description."));
            items.Add(Missing.Count == 0
                ? new("Looks right", SystemCheck.Status.Ok, $"It mentions {List(Expected.Select(e => e.Thing))}.")
                : new("Looks right", SystemCheck.Status.Info,
                    $"It doesn't mention {List(Missing)}, which the sample shows. Descriptions vary, so this isn't necessarily a problem."));
        }
        else
        {
            items.Add(new("Result", SystemCheck.Status.Problem, $"{message?.TrimEnd('.')}. ({code})", messageIsPrivate ? $"({code})" : null));
        }
        if (took > TimeSpan.Zero) items.Add(new("Time", SystemCheck.Status.Info, Seconds(took) + "."));
        var title = WindowsAI.Kinds.FirstOrDefault(k => k.Id == kind).Title ?? kind;
        items.Add(new("Description type", SystemCheck.Status.Info, $"{title}. Change it in the Photo menu."));
        if (readiness?.HelperVersion is { } helper)
            items.Add(new("AI helper", SystemCheck.Status.Info, $"Version {helper}" + (readiness.Protocol is { } p ? $", protocol {p}." : ".")));
        items.Add(new("Sample photo", SystemCheck.Status.Info, $"{SampleShows}. Drawn for {AppInfo.Name}; nothing on this PC was changed or saved."));
        Items = items;
    }

    public string Kind { get; }
    public TimeSpan Took { get; }
    public string? Description { get; }
    public string? Code { get; }
    public string? Message { get; }
    public bool Worked => Description is not null;
    /// <summary>The failure's message may name a folder, so a bug report leaves it out.</summary>
    public bool MessageIsPrivate { get; }

    /// <summary>What the sample shows that the description doesn't mention.</summary>
    public IReadOnlyList<string> Missing { get; }

    public IReadOnlyList<SystemCheck.Item> Items { get; }

    public string Summary => Worked
        ? $"Windows AI described the sample photo in {Seconds(Took)}."
        // The reason is the first line, where focus lands, so it isn't said twice.
        : "Windows AI couldn't describe the sample photo.";

    public string Text => string.Join(Environment.NewLine, Items.Select(i => i.Line).Prepend(Summary));

    /// <summary>For a bug report, which is public: the same, without a message that might name a folder.</summary>
    public string ReportText => Privacy.Redact(string.Join(Environment.NewLine, Items.Select(i => i.ReportLine).Prepend(Summary)));

    /// <summary>One line for Check System and bug reports.</summary>
    public string ReportSummary => Worked
        ? $"Worked in {Seconds(Took)}" + (Missing.Count == 0 ? ", and mentions what the sample shows." : $", but doesn't mention {List(Missing)}.")
        : MessageIsPrivate ? $"Failed: {Code}." : Privacy.Redact($"Failed: {Code}: {Message?.TrimEnd('.')}.");

    /// <summary>The word on its own or as a plural ("mugs"), not inside another word ("reopened").</summary>
    private static bool Mentions(string text, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{word}s?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string List(IEnumerable<string> things)
    {
        var list = things.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} seconds";
}
