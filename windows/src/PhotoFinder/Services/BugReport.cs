using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoFinder.Services;

/// <summary>
/// Help > Report a Bug… (#33, #138), as on the Mac (#137): what the form collects, the text it sends, and how it's sent.
/// The app kit's relay (a Cloudflare Worker run by The Idea Place) files it as a GitHub issue with a bot account, so
/// nobody needs a GitHub account to report a bug. When the relay can't be reached, or this copy was built without a
/// relay key, the prefilled GitHub issue page opens instead, with the whole report on the clipboard. Kept free of WPF
/// and the network, so it can be tested; the form is BugReportWindow.
/// </summary>
public sealed class BugReport
{
    public enum Kinds { Bug, Suggestion }

    /// <summary>Every report's title starts with this, so Windows' reports can be told from the Mac's at a glance.</summary>
    public const string TitlePrefix = "[Windows] ";

    public Kinds Kind { get; set; } = Kinds.Bug;
    public string Title { get; set; } = "";
    public string Happened { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Steps { get; set; } = "";
    /// <summary>A suggestion's one field.</summary>
    public string Suggestion { get; set; } = "";
    /// <summary>Optional, and public like everything else in the report.</summary>
    public string Contact { get; set; } = "";
    public bool IncludeSystemCheck { get; set; } = true;
    /// <summary>Check System's report text, already without folder paths.</summary>
    public string SystemText { get; set; } = "";
    /// <summary>What came with the report from elsewhere: Check AI Description's result, or the last crash. Redacted
    /// already.</summary>
    public List<Attachment> Attachments { get; } = [];
    public bool IncludeAttachments { get; set; } = true;

    /// <param name="Name">What it's called in the form's check box: "the crash report".</param>
    public sealed record Attachment(string Name, string Heading, string Text);

    public static string KindTitle(Kinds kind) => kind == Kinds.Bug ? "Bug" : "Suggestion";

    /// <summary>GitHub's labels, as the relay applies them; the fallback page asks for the same (only people who can
    /// triage the repo get them, hence "[Windows]" in the title too).</summary>
    public string Labels => Kind == Kinds.Bug ? "bug,user-reported,windows" : "enhancement,user-reported,windows";

    /// <summary>The check box for what came with the report: "Include the log".</summary>
    public string AttachmentsTitle => "Include " + Joined(Attachments.Select(a => a.Name).Distinct().ToList());

    public string FullTitle => TitlePrefix + (Title.Trim() is { Length: > 0 } t ? t : Kind == Kinds.Bug ? "Bug report" : "Suggestion");

    public string? TrimmedContact => Contact.Trim() is { Length: > 0 } c ? c : null;

    /// <summary>The issue's text: the questions, then the system check, then anything attached. An empty answer is left
    /// out rather than sent as an empty heading.</summary>
    public string Body
    {
        get
        {
            var sections = new List<string>();
            void Add(string heading, string text)
            {
                if (text.Trim() is { Length: > 0 } t) sections.Add($"**{heading}**\n{t}");
            }
            if (Kind == Kinds.Bug)
            {
                Add("What happened", Happened);
                Add("What you expected", Expected);
                Add("Steps", Steps);
            }
            else Add("Suggestion", Suggestion);
            if (IncludeSystemCheck && SystemText.Length > 0) sections.Add($"**System**\n```\n{SystemText}\n```");
            if (IncludeAttachments)
                foreach (var a in Attachments.Where(a => a.Text.Length > 0)) sections.Add($"**{a.Heading}**\n```\n{a.Text}\n```");
            return sections.Count == 0 ? "(No details given.)" : string.Join("\n\n", sections);
        }
    }

    /// <summary>Exactly what Send sends, for the form's preview: the title, the kind, the contact if there is one, and
    /// the body.</summary>
    public string Preview
    {
        get
        {
            var lines = new List<string> { $"Title: {FullTitle}", $"Kind: {KindTitle(Kind)}" };
            if (TrimmedContact is { } c) lines.Add($"Contact: {c}");
            return string.Join("\n", lines) + "\n\n" + Body;
        }
    }

    /// <summary>The whole report, for the clipboard when the browser page is used instead.</summary>
    public string ClipboardText => FullTitle + "\n\n" + Body;

    /// <summary>The relay's JSON: {title, body, contact?, kind}.</summary>
    public string RelayJson()
    {
        var o = new JsonObject { ["title"] = FullTitle, ["body"] = Body, ["kind"] = Kind == Kinds.Bug ? "bug" : "suggestion" };
        if (TrimmedContact is { } c) o["contact"] = c;
        return o.ToJsonString();
    }

    /// <summary>On the GitHub page only, where a file can be attached; the relay takes text alone.</summary>
    public const string LogNote = "Help > Open Log Folder has a log of what went wrong, which can be attached here. It names files and folders, so look at it first: issues are public.";

    /// <summary>Characters of body the GitHub page is given, which keeps its address well under GitHub's limit once
    /// encoded; the whole report is on the clipboard.</summary>
    public const int LongestPageBody = 5000;

    /// <summary>The prefilled GitHub page used when the relay isn't.</summary>
    public Uri IssuePageUri()
    {
        var body = Body + "\n\n" + LogNote;
        if (body.Length > LongestPageBody) body = body[..LongestPageBody] + "\n\n(Cut short here: paste the rest from the clipboard.)";
        return new Uri($"{AppInfo.IssuesUrl}?title={Uri.EscapeDataString(FullTitle)}&body={Uri.EscapeDataString(body)}&labels={Uri.EscapeDataString(Labels)}");
    }

    private static string Joined(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}

/// <summary>Sending a report to the app kit's relay. The network is a function, so tests send nothing.</summary>
public static class BugReportRelay
{
    public const string Url = "https://appkit-bug-relay.quickmail.workers.dev/report";
    public const string AppId = "photofinder";

    /// <summary>The relay gives up at 12 seconds; the apps at 15 (the relay's README).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Filed, with the issue's number and address; or not (NoKey when this copy has no relay key). Either of
    /// the last opens the browser page.</summary>
    public sealed record Result(int Number, Uri? IssueUrl, bool NoKey = false)
    {
        public bool Filed => IssueUrl is not null;
    }

    /// <summary>The file beside the app holding the relay key, written by the build from the repository's secret (or
    /// the signing kit, on Kelly's PC). Missing in a development build.</summary>
    public const string KeyFileName = "appkit-relay.key";

    /// <summary>The relay key this copy was built with, or null.</summary>
    public static string? Key { get; set; } = ReadKey(Path.Combine(AppContext.BaseDirectory, KeyFileName));

    internal static string? ReadKey(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } key ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = Timeout };

    public delegate Task<(int Status, string Body)> Transport(HttpRequestMessage request, CancellationToken cancel);

    private static async Task<(int, string)> Send(HttpRequestMessage request, CancellationToken cancel)
    {
        using var response = await Http.SendAsync(request, cancel);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>The POST to the relay, with the app's id and key. The key goes in a header and nowhere else.</summary>
    public static HttpRequestMessage Request(BugReport report, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(report.RelayJson(), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-AppKit-App", AppId);
        request.Headers.Add("X-AppKit-Key", key);
        return request;
    }

    /// <summary>The relay's answer, {issueUrl, number}, or null for anything else.</summary>
    internal static Result? Filed(int status, string body)
    {
        if (status is < 200 or > 299) return null;
        try
        {
            var o = JsonNode.Parse(body) as JsonObject;
            return o?["number"] is JsonValue n && n.TryGetValue<int>(out var number) && number > 0
                   && o["issueUrl"] is JsonValue u && u.TryGetValue<string>(out var address)
                   && Uri.TryCreate(address, UriKind.Absolute, out var url) && url.Scheme == "https"
                ? new Result(number, url)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<Result> SendAsync(BugReport report, string? key, Transport? transport = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(key)) return new Result(0, null, NoKey: true);
        try
        {
            using var request = Request(report, key);
            var (status, body) = await (transport ?? Send)(request, cancel);
            if (Filed(status, body) is { } filed) return filed;
            Log.Write("report", $"The bug-report relay answered {status}; opening the GitHub page instead");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Log.Write("report", $"The bug-report relay couldn't be reached ({ex.GetType().Name}); opening the GitHub page instead");
        }
        return new Result(0, null);
    }
}

/// <summary>
/// Whether the app quit cleanly last time (#33), as the app kit asks: a flag file in the data folder, written at start
/// and removed when the app quits normally. Still there at the next start means the app crashed, was ended, or the PC
/// lost power, and the app offers once to report it.
/// </summary>
public static class CrashCheck
{
    public static string FlagPath => Path.Combine(AppPaths.DataFolder, "Running.flag");

    /// <summary>Called at start: when the last run didn't quit cleanly, when it started (the flag's date), otherwise
    /// null. Then writes the flag for this run.</summary>
    public static DateTime? Begin(Func<int, bool>? isRunning = null, int? processId = null)
    {
        var pid = processId ?? Environment.ProcessId;
        isRunning ??= IsRunning;
        DateTime? previous = null;
        try
        {
            if (File.Exists(FlagPath))
            {
                // The flag names the process that wrote it. If that copy is still running (a second copy opened), it
                // didn't crash, so there's nothing to offer.
                if (int.TryParse(File.ReadAllText(FlagPath).Trim(), out var owner) && owner != pid && isRunning(owner)) return null;
                previous = File.GetLastWriteTime(FlagPath);
            }
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(FlagPath, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell; nothing to offer.
        }
        return previous;
    }

    /// <summary>Called as the app quits normally.</summary>
    public static void End()
    {
        try { File.Delete(FlagPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left; offered next time */ }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("PhotoFinder", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>What goes with a crash report: the last 40 lines of the log from the day of the crashed run (and today's,
    /// when that's another day), which has the crash itself when there was one. Without the user folder.</summary>
    public static List<BugReport.Attachment> Attachments(DateTime since, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var days = since.Date == today ? new[] { today } : [since.Date, today];
        var attachments = new List<BugReport.Attachment>();
        foreach (var day in days)
        {
            var name = $"PhotoFinder-{day:yyyy-MM-dd}.log";
            var path = Path.Combine(Log.Folder, name);
            try
            {
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                attachments.Add(new("the log", $"Diagnostic log, {name} (last 40 lines)", Privacy.Redact(string.Join("\n", lines.TakeLast(40)))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out.
            }
        }
        return attachments;
    }
}
