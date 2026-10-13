using System.Net.Http;
using PhotoFinder.Services;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Help > Report a Bug through the app kit's relay (#138), as on the Mac (#137), with a stand-in for the relay
/// so nothing is sent.</summary>
[Collection(SharedState.Name)]
public sealed class BugReportTests : IDisposable
{
    private readonly List<string> _announced = [];
    private readonly List<string> _opened = [];
    private readonly Action<string> _openWas = MainWindow.OpenUrl;

    public BugReportTests()
    {
        Announcer.Observer = _announced.Add;
        MainWindow.OpenUrl = _opened.Add;
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        MainWindow.OpenUrl = _openWas;
    }

    private static void Click(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static string Help(System.Windows.DependencyObject element) => System.Windows.Automation.AutomationProperties.GetHelpText(element);

    private static BugReport Report() => new()
    {
        Title = "Gallery reads twice",
        Happened = "It said the name twice.",
        Expected = "Once.",
        Steps = "1. Ctrl+4\n2. Right Arrow",
        SystemText = "Everything needed to describe photos is ready.\nWindows: OK.",
    };

    [Fact]
    public void TheReport_HasTheQuestions_TheSystem_AndATitleMarkedWindows()
    {
        var report = Report();
        Assert.Equal("[Windows] Gallery reads twice", report.FullTitle);
        Assert.Equal("**What happened**\nIt said the name twice.\n\n**What you expected**\nOnce.\n\n**Steps**\n1. Ctrl+4\n2. Right Arrow\n\n**System**\n```\nEverything needed to describe photos is ready.\nWindows: OK.\n```",
            report.Body);
        Assert.StartsWith("Title: [Windows] Gallery reads twice\nKind: Bug\n\n**What happened**", report.Preview);

        // Empty answers are left out, and with nothing at all it says so.
        var empty = new BugReport { IncludeSystemCheck = false };
        Assert.Equal("[Windows] Bug report", empty.FullTitle);
        Assert.Equal("(No details given.)", empty.Body);

        var suggestion = new BugReport { Kind = BugReport.Kinds.Suggestion, Suggestion = "Read the date too.", Contact = " me@example.com " };
        Assert.Equal("[Windows] Suggestion", suggestion.FullTitle);
        Assert.StartsWith("**Suggestion**\nRead the date too.", suggestion.Body);
        Assert.Contains("Contact: me@example.com", suggestion.Preview);
        Assert.Equal("enhancement,user-reported,windows", suggestion.Labels);
    }

    [Fact]
    public void Attachments_AreIncludedUnlessUnticked()
    {
        var report = Report();
        report.Attachments.Add(new("the log", "Diagnostic log, PhotoFinder-2026-10-13.log (last 40 lines)", "12:00 [crash] Unhandled"));
        Assert.Equal("Include the log", report.AttachmentsTitle);
        Assert.Contains("**Diagnostic log, PhotoFinder-2026-10-13.log (last 40 lines)**\n```\n12:00 [crash] Unhandled\n```", report.Body);
        report.IncludeAttachments = false;
        Assert.DoesNotContain("[crash]", report.Body);

        // When the answers nearly fill the report, an attachment that doesn't fit says so, if that note fits.
        report.Attachments[0] = report.Attachments[0] with { Text = new string('y', 600) };
        report.Happened = "";
        report.IncludeAttachments = false;
        // About 150 characters left: room for the note, not for the 600-character line.
        report.Happened = new string('x', BugReport.LongestBody - report.Body.Length - 150);
        report.IncludeAttachments = true;
        Assert.EndsWith("**Diagnostic log, PhotoFinder-2026-10-13.log (last 40 lines)**\n" + BugReport.DidntFit, report.Body);
        Assert.True(report.Body.Length <= BugReport.LongestBody);
        // With no room even for that, it's left out without a word, and the body still fits (#154).
        report.Happened = new string('x', BugReport.LongestBody);
        Assert.DoesNotContain(BugReport.DidntFit, report.Body);
        Assert.DoesNotContain("Diagnostic log", report.Body);
    }

    [Fact]
    public void ALongLog_LosesItsOldestLines_SoTheRelayKeepsTheCrash()
    {
        var report = Report();
        var log = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"12:00:{i:000} [describe] line {i} " + new string('x', 80))) + "\n12:01 [crash] Unhandled: the end";
        report.Attachments.Add(new("the log", "Diagnostic log", log));
        var body = report.Body;
        Assert.True(body.Length <= BugReport.LongestBody, $"{body.Length}");
        Assert.Contains("[crash] Unhandled: the end\n```", body);
        Assert.Contains("```\n" + BugReport.LinesLeftOut + "\n", body);
        Assert.DoesNotContain("line 1 ", body);
        Assert.Contains("**System**", body);
        // The preview is what's sent.
        Assert.EndsWith(body, report.Preview);
    }

    [Fact]
    public void TheRelayGetsTheReport_TheAppsIdAndKey_InHeadersOnly()
    {
        var report = Report();
        report.Contact = "me@example.com";
        using var request = BugReportRelay.Request(report, "secret-key");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BugReportRelay.Url, request.RequestUri!.AbsoluteUri);
        Assert.Equal("photofinder", request.Headers.GetValues("X-AppKit-App").Single());
        Assert.Equal("secret-key", request.Headers.GetValues("X-AppKit-Key").Single());
        var json = System.Text.Json.Nodes.JsonNode.Parse(report.RelayJson())!;
        Assert.Equal("[Windows] Gallery reads twice", (string)json["title"]!);
        Assert.Equal("bug", (string)json["kind"]!);
        Assert.Equal("me@example.com", (string)json["contact"]!);
        Assert.DoesNotContain("secret-key", report.RelayJson());
    }

    [Fact]
    public async Task Sending_FilesItThroughTheRelay_OrFallsBack()
    {
        var filed = await BugReportRelay.SendAsync(Report(), "key", (_, _) => Task.FromResult((201, """{"issueUrl":"https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues/150","number":150}""")));
        Assert.True(filed.Filed);
        Assert.Equal(150, filed.Number);

        Assert.True((await BugReportRelay.SendAsync(Report(), null)).NoKey);
        Assert.False((await BugReportRelay.SendAsync(Report(), "key", (_, _) => Task.FromResult((502, "Bad gateway")))).Filed);
        Assert.False((await BugReportRelay.SendAsync(Report(), "key", (_, _) => throw new HttpRequestException("Offline"))).Filed);
        // Only an https issue address counts.
        Assert.False((await BugReportRelay.SendAsync(Report(), "key", (_, _) => Task.FromResult((200, """{"issueUrl":"http://example.com","number":1}""")))).Filed);
    }

    [Fact]
    public void TheGitHubPage_IsPrefilled_PointsAtTheLog_AndIsCutShortWhenLong()
    {
        var uri = Report().IssuePageUri().AbsoluteUri;
        Assert.StartsWith(AppInfo.IssuesUrl + "?title=%5BWindows%5D%20Gallery%20reads%20twice&body=", uri);
        Assert.EndsWith("&labels=bug%2Cuser-reported%2Cwindows", uri);
        Assert.Contains(Uri.EscapeDataString("Help > Open Log Folder has a log"), uri);
        var long_ = Report();
        long_.Happened = new string('x', 9000);
        Assert.Contains(Uri.EscapeDataString("(Cut short here: paste the rest from the clipboard.)"), long_.IssuePageUri().AbsoluteUri);
        Assert.Contains(Uri.EscapeDataString("(Cut short here: copy the rest from the report in"), long_.IssuePageUri(copied: false).AbsoluteUri);

        // It's the address's encoded length that's kept short: 2,000 Japanese characters are 18,000 once encoded.
        var japanese = Report();
        japanese.Happened = new string('写', 2000);
        var address = japanese.IssuePageUri().AbsoluteUri;
        Assert.True(address.Length <= BugReport.LongestPageAddress, $"{address.Length}");
        Assert.Contains(Uri.EscapeDataString("(Cut short here"), address);
        Assert.EndsWith("&labels=bug%2Cuser-reported%2Cwindows", address);
    }

    [Fact]
    public void TheRelayKey_IsReadFromBesideTheApp_WhenThereIsOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}.key");
        Assert.Null(BugReportRelay.ReadKey(path));
        File.WriteAllText(path, "  abc123\n");
        try { Assert.Equal("abc123", BugReportRelay.ReadKey(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ACrash_IsTold_ByAFlagLeftFromTheLastRun_ButNotByACopyStillRunning()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.Delete(CrashCheck.FlagPath);
        Assert.Null(CrashCheck.Begin(_ => false, processId: 1));
        // Not removed: the next start finds it.
        Assert.NotNull(CrashCheck.Begin(_ => false, processId: 2));
        // A second copy, while the first is still running: not a crash, and its quitting leaves the first one's flag.
        Assert.Null(CrashCheck.Begin(pid => pid == 2, processId: 3));
        CrashCheck.End(processId: 3);
        Assert.True(File.Exists(CrashCheck.FlagPath));
        CrashCheck.End(processId: 2);
        Assert.False(File.Exists(CrashCheck.FlagPath));
        Assert.Null(CrashCheck.Begin(_ => false, processId: 4));
        CrashCheck.End(processId: 4);
        // A flag naming a process number since reused by another program (Windows' System, 4) is still a crash; one
        // naming a copy of the app still running (this test host, PhotoFinder.Tests) isn't.
        Assert.Null(CrashCheck.Begin(processId: 4));
        Assert.NotNull(CrashCheck.Begin(processId: Environment.ProcessId));
        Assert.Null(CrashCheck.Begin(processId: 5));
        CrashCheck.End();
        CrashCheck.End(processId: 5);
        Assert.False(File.Exists(CrashCheck.FlagPath));
    }

    [Fact]
    public void ACrashReport_HasTheNewestLogsLastLines_CutAndWithoutTheUserFolder()
    {
        Directory.CreateDirectory(Log.Folder);
        foreach (var f in Directory.EnumerateFiles(Log.Folder, "PhotoFinder-*.log")) File.Delete(f);
        Assert.Empty(CrashCheck.Attachments());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllLines(Path.Combine(Log.Folder, "PhotoFinder-2026-10-01.log"), ["old"]);
        File.WriteAllLines(Path.Combine(Log.Folder, "PhotoFinder-2026-10-12.log"),
            [.. Enumerable.Range(1, 50).Select(i => $"line {i}"), $"[crash] Unhandled in {home}\\Pictures " + new string('x', 3000)]);
        try
        {
            var a = Assert.Single(CrashCheck.Attachments());
            Assert.Equal("Diagnostic log, PhotoFinder-2026-10-12.log (last 40 lines)", a.Heading);
            var lines = a.Text.Split('\n');
            Assert.Equal(40, lines.Length);
            Assert.Equal("line 12", lines[0]);
            Assert.StartsWith(@"[crash] Unhandled in %USERPROFILE%\Pictures x", lines[^1]);
            Assert.EndsWith(" …", lines[^1]);
            Assert.True(lines[^1].Length < CrashCheck.LongestLogLine + 10);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Log.Folder, "PhotoFinder-2026-10-*.log")) File.Delete(f);
        }
    }

    [WpfFact]
    public async Task TheForm_FocusesTheTitle_PreviewsWhatsSent_AndSaysWhenItsFiled()
    {
        var sent = 0;
        var window = new BugReportWindow(Report(), "key", (request, _) =>
        {
            sent++;
            return Task.FromResult((201, """{"issueUrl":"https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues/151","number":151}"""));
        }) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.False(window.SendButton.IsDefault);
            Assert.Equal(180, window.TitleBox.MaxLength);
            Assert.Equal("", Help(window.SendButton));
            Assert.Equal(BugReportWindow.ContactNoteText, Help(window.ContactBox));
            window.TitleBox.Text = "Something else";
            Assert.StartsWith("Title: [Windows] Something else", window.PreviewBox.Text);
            window.SuggestionKind.IsChecked = true;
            Assert.Equal(System.Windows.Visibility.Collapsed, window.BugFields.Visibility);
            window.BugKind.IsChecked = true;
            await window.SendAsync();
            Assert.Equal(1, sent);
            Assert.Equal("Report filed as issue #151", window.FiledHeading.Text);
            // Said where focus lands, not announced over it.
            Assert.Equal("Report filed as issue #151. Thank you. It's public on GitHub, where you can follow it.", Help(window.OpenIssueButton));
            Assert.Equal(["Sending"], _announced);
            Click(window.OpenIssueButton);
            Assert.Equal(["https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues/151"], _opened);
            // Escape closes the result.
            Assert.Single(((System.Windows.Controls.Panel)window.OpenIssueButton.Parent).Children.OfType<System.Windows.Controls.Button>(), b => b.IsCancel);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task WithoutAKey_TheFormGoesToGitHub_WithTheReportOnTheClipboard()
    {
        var window = new BugReportWindow(Report(), null) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.Equal(System.Windows.Visibility.Visible, window.NoKeyText.Visibility);
            Assert.Equal(BugReportWindow.NoKeyNote, Help(window.SendButton));
            await window.SendAsync();
            Assert.Equal("Ready to submit on GitHub", window.NotSentHeading.Text);
            Assert.StartsWith("Ready to submit on GitHub. ", Help(window.OpenInBrowserButton));
            Assert.Single(((System.Windows.Controls.Panel)window.OpenInBrowserButton.Parent).Children.OfType<System.Windows.Controls.Button>(), b => b.IsCancel);
            Click(window.OpenInBrowserButton);
            Assert.StartsWith(AppInfo.IssuesUrl + "?title=", _opened.Single());
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    [WpfFact]
    public async Task ClosingWhileSending_Waits_SoTheAnswerIsntLost()
    {
        var answer = new TaskCompletionSource<(int, string)>();
        var window = new BugReportWindow(Report(), "key", (_, _) => answer.Task) { ShowActivated = false };
        window.Show();
        try
        {
            var sending = window.SendAsync();
            Assert.Equal(System.Windows.Visibility.Visible, window.SendingText.Visibility);
            await window.SendAsync();
            Assert.Equal(["Sending", "Still sending"], _announced);
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal("Still sending", _announced[^1]);
            answer.SetResult((502, ""));
            await sending;
            Assert.Equal("Couldn't send the report", window.NotSentHeading.Text);
        }
        finally
        {
            window.Close();
        }
        Assert.False(window.IsVisible);
    }

    [WpfFact]
    public void About_StartsOnClose_SayingWhatItIs_WithItsLinks()
    {
        var opened = new List<string>();
        var window = new AboutWindow((url, _) => { opened.Add(url); return true; }) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.True(window.CloseButton.IsDefault && window.CloseButton.IsCancel);
            Assert.Equal($"{AppInfo.Name}, version {Updates.AppVersion}. {AboutWindow.What}", Help(window.CloseButton));
            Click(window.SourceButton);
            Assert.Equal([$"https://github.com/{AppInfo.Repository}"], opened);
        }
        finally
        {
            window.Close();
        }
    }
}
