using System.Windows;
using System.Windows.Controls;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Help > Check System and Report a Bug, with the same layout and wording as the Mac's (#32, #33).</summary>
[Collection(SharedState.Name)]
public sealed class SystemCheckTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private static SystemCheck.Facts Good() => new()
    {
        AppVersion = "0.1.0.140",
        UpdatesItself = true,
        OsVersion = new Version(10, 0, 26100),
        Npu = "Qualcomm Hexagon NPU",
        Readiness = new AIReadiness(true, "Ready", null),
        LibraryFolders = [@"C:\Users\me\Pictures"],
        SavedDescriptions = 1234,
        DescribeAutomatically = true,
    };

    [Fact]
    public void ACopilotPcWithEverything_IsReady_OneLinePerCheck()
    {
        var check = new SystemCheck(Good());
        Assert.Equal("Everything needed to describe photos is ready.", check.Summary);
        Assert.Equal(
        [
            $"{AppInfo.Name}: Version 0.1.0.140. Updates itself: Help > Check for Updates checks now.",
            "Windows: OK. Build 26100.",
            "NPU: OK. Qualcomm Hexagon NPU.",
            "Windows AI: OK. Ready to describe photos, on this PC.",
            @"Library folders: OK. 1 folder: C:\Users\me\Pictures.",
            "Saved descriptions: 1,234 photos described.",
            "Describe Photos Automatically: On.",
            "Describe Added Folders in the Background: Off.",
        ], check.Items.Select(i => i.Line));
        Assert.StartsWith(check.Summary + Environment.NewLine, check.Text);
        // Screen readers name each list item from ToString.
        Assert.Equal(check.Items[1].Line, check.Items[1].ToString());
    }

    [Fact]
    public void AnOlderWindows_IsOneProblem_AndWindowsAiIsntCheckedTwice()
    {
        var check = new SystemCheck(Good() with { OsVersion = new Version(10, 0, 22631) });
        Assert.Equal("1 thing needs attention.", check.Summary);
        Assert.Contains("Windows: Needs attention. Build 22631. Describing photos needs Windows 11 version 24H2 (build 26100) or later. Browsing works on this version.",
            check.Items.Select(i => i.Line));
        Assert.Contains("Windows AI: Not checked: it needs a newer Windows.", check.Items.Select(i => i.Line));
    }

    [Fact]
    public void NoNpu_SaysItNeedsACopilotPc()
    {
        var check = new SystemCheck(Good() with { Npu = null });
        Assert.Equal("1 thing needs attention.", check.Summary);
        Assert.Contains("NPU: Needs attention. None found. Windows describes photos only on a Copilot+ PC, which has an NPU.", check.Items.Select(i => i.Line));
        Assert.Contains("Windows AI: Not checked: it needs an NPU.", check.Items.Select(i => i.Line));
    }

    [Fact]
    public void WindowsAiNotReadyOrMissing_SaysWhatToDo()
    {
        var notReady = new SystemCheck(Good() with { Readiness = new AIReadiness(false, "NotReady", "x") });
        Assert.Contains("Windows AI: Needs attention. Windows' image description model isn't downloaded yet. Describe Photo (Ctrl+Shift+D) downloads it.",
            notReady.Items.Select(i => i.Line));
        var missing = new SystemCheck(Good() with { Readiness = new AIReadiness(false, "NotInstalled", "x") });
        Assert.Contains($"Windows AI: Needs attention. The {AppInfo.HelperName} isn't installed. Install {AppInfo.Name} from PhotoFinder.appinstaller.",
            missing.Items.Select(i => i.Line));
        var other = new SystemCheck(Good() with { Readiness = new AIReadiness(false, "DisabledByUser", "Turn it on in Settings.") });
        Assert.Contains("Windows AI: Needs attention. Turn it on in Settings.", other.Items.Select(i => i.Line));
    }

    [Fact]
    public void FolderProblems_AreCountedTogether()
    {
        var check = new SystemCheck(Good() with
        {
            LibraryFolders = [@"C:\Pics", @"\\nas\photos"],
            MissingFolders = [@"\\nas\photos"],
            UpdatesItself = false,
            Readiness = new AIReadiness(false, "NotReady", null),
            SavedDescriptions = 1,
            DescribeAutomatically = false,
            DescribeInBackground = true,
        });
        Assert.Equal("2 things need attention.", check.Summary);
        var lines = check.Items.Select(i => i.Line).ToList();
        Assert.Contains(@"Library folders: Needs attention. 2 folders, but \\nas\photos can't be reached. Connect the drive, or remove the folder with File > Remove Folder from Library.", lines);
        Assert.Contains($"{AppInfo.Name}: Version 0.1.0.140. Not installed with its installer, so it doesn't update itself.", lines);
        Assert.Contains("Saved descriptions: 1 photo described.", lines);
        Assert.Contains("Describe Photos Automatically: Off. Turn it on in the Photo menu.", lines);
        Assert.Contains("Describe Added Folders in the Background: On.", lines);
        Assert.Contains("Library folders: Needs attention. None. Add one with File > Add Folder to Library.",
            new SystemCheck(Good() with { LibraryFolders = [] }).Items.Select(i => i.Line));
    }

    [Fact]
    public void ABugReport_HasTheQuestionsAndTheSystem_AndOpensAsAWindowsIssue()
    {
        var body = BugReport.Body("Everything needed to describe photos is ready.\nWindows: OK. Build 26100.");
        Assert.Contains("**What happened**", body);
        Assert.Contains("**What you expected**", body);
        Assert.Contains("**Steps**", body);
        Assert.Contains("```\nEverything needed to describe photos is ready.\nWindows: OK. Build 26100.\n```", body.Replace("\r\n", "\n"));
        var uri = BugReport.NewIssueUri(body).AbsoluteUri;
        Assert.StartsWith(AppInfo.IssuesUrl + "?title=%5BWindows%5D%20&body=", uri);
        Assert.EndsWith("&labels=bug%2Cuser-reported%2Cwindows", uri);
    }

    [Fact]
    public void SavedDescriptions_AreCountedByPhoto_NotByType()
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        Assert.Equal(0, store.PhotoCount);
        MediaItem Item(string name)
        {
            var info = new FileInfo(_lib.AddJpeg(name));
            return new MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime);
        }
        var a = Item("a.jpg");
        store.Put(a, "accessible", "one");
        store.Put(a, "brief", "two");
        store.Put(Item("b.jpg"), "accessible", "three");
        Assert.Equal(2, store.PhotoCount);
    }

    [WpfFact]
    public async Task TheWindow_ListsTheChecks_AndChecksAgain()
    {
        var runs = 0;
        var window = new CheckSystemWindow(() => { runs++; return Task.FromResult(new SystemCheck(Good() with { Npu = runs > 1 ? null : "NPU" })); }, _ => { })
            { ShowActivated = false };
        window.Show();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (window.Result is null && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.Equal("Everything needed to describe photos is ready.", window.SummaryText.Text);
            Assert.Equal(8, window.ChecksList.Items.Count);
            await window.RefreshAsync();
            Assert.Equal(2, runs);
            Assert.Equal("1 thing needs attention.", window.SummaryText.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
