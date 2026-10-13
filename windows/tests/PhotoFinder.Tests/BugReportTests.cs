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
    private readonly Action<string> _openWas = BugReportWindow.OpenUrl;

    public BugReportTests()
    {
        Announcer.Observer = _announced.Add;
        BugReportWindow.OpenUrl = _opened.Add;
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        BugReportWindow.OpenUrl = _openWas;
    }

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
        CrashCheck.End();
        Assert.Null(CrashCheck.Begin(_ => false, processId: 1));
        // Not removed: the next start finds it.
        Assert.NotNull(CrashCheck.Begin(_ => false, processId: 2));
        // A second copy, while the first is still running: not a crash.
        Assert.Null(CrashCheck.Begin(pid => pid == 2, processId: 3));
        CrashCheck.End();
        Assert.False(File.Exists(CrashCheck.FlagPath));
        Assert.Null(CrashCheck.Begin(_ => false, processId: 4));
        CrashCheck.End();
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
            Assert.Equal(BugReportWindow.ContactNoteText, System.Windows.Automation.AutomationProperties.GetHelpText(window.ContactBox));
            window.TitleBox.Text = "Something else";
            Assert.StartsWith("Title: [Windows] Something else", window.PreviewBox.Text);
            window.SuggestionKind.IsChecked = true;
            Assert.Equal(System.Windows.Visibility.Collapsed, window.BugFields.Visibility);
            window.BugKind.IsChecked = true;
            await window.SendAsync();
            Assert.Equal(1, sent);
            Assert.Equal("Report filed as issue #151", window.FiledHeading.Text);
            Assert.Contains("Report filed as issue #151", _announced);
            window.OpenIssueButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(["https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues/151"], _opened);
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
            await window.SendAsync();
            Assert.Equal("Ready to submit on GitHub", window.NotSentHeading.Text);
            window.OpenInBrowserButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.StartsWith(AppInfo.IssuesUrl + "?title=", _opened.Single());
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }
}
