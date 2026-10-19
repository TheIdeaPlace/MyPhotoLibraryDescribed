using System.Windows.Controls;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Check System's details about the AI in use (#94), and Help > Check AI Description (#95), with stand-ins for
/// Windows AI so they run anywhere.</summary>
[Collection(SharedState.Name)]
public sealed class AIDetailsTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;
    private readonly List<string> _announced = [];

    public AIDetailsTests()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("home.jpg");
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        System.Globalization.CultureInfo.CurrentCulture = _culture;
        _lib.Dispose();
    }

    private static readonly AIReadiness Ready = new(true, "Ready", null)
    {
        HelperVersion = "0.1.0",
        Protocol = 1,
        Components = new Dictionary<string, string> { ["textRecognition"] = "Ready", ["languageModel"] = "NotReady" },
    };

    private static SystemCheck.Facts Facts() => new()
    {
        AppVersion = "0.1.0.600",
        OsVersion = new Version(10, 0, 26340),
        Npu = "Qualcomm Hexagon NPU",
        Readiness = Ready,
        LibraryFolders = [@"C:\Users\me\Pictures"],
        WindowsEdition = "Windows 11 Pro 26H2, build 26340.9616",
        Processor = "Snapdragon X 12-core X1E80100 @ 3.40 GHz, Arm64",
        Memory = "32 GB",
        Power = "Plugged in, battery 80%, energy saver off",
        NpuDriver = "version 30.0.220.3000 from Qualcomm Technologies, Inc., dated 1/8/2026",
        PackageName = "PhotoFinder_0.1.0.600_arm64__abc",
        AppRuntime = "Microsoft.WindowsAppRuntime.1.8 8000.994.2142.0",
        AIPackages = [("MicrosoftWindows.Client.CoreAI", "1000.26100.9616.0"), ("WindowsWorkload.EP.Qualcomm.QNN.1.8", "1.8.41.0")],
        DescribeTimings = "12 descriptions, typically 3.1 seconds, slowest 9.8 seconds.",
        RecentFailures = ["6:30 PM internal_error: Windows couldn't describe this picture."],
        AIDescriptionCheck = "Worked in 9.8 seconds, and mentions what the sample shows.",
    };

    // MARK: Check System's details (#94)

    [Fact]
    public void CheckSystem_ShowsTheDetails_OneLineEach_InOrder()
    {
        var check = new SystemCheck(Facts());
        Assert.Equal("Everything needed to describe photos is ready.", check.Summary);
        Assert.Equal(
        [
            $"{AppInfo.Name}: Version 0.1.0.600. Not installed with its installer, so it doesn't update itself.",
            "Windows: OK. Windows 11 Pro 26H2, build 26340.9616.",
            "NPU: OK. Qualcomm Hexagon NPU. Driver version 30.0.220.3000 from Qualcomm Technologies, Inc., dated 1/8/2026.",
            "Windows AI: OK. Ready to describe photos, on this PC.",
            @"Library folders: OK. 1 folder: C:\Users\me\Pictures.",
            "Saved descriptions: None yet.",
            "Describe Photos Automatically: Off. Turn it on in the Photo menu.",
            "Describe Photos in the Background: Off.",
            // The details after the checks, so anything to do comes first.
            "Processor: Snapdragon X 12-core X1E80100 @ 3.40 GHz, Arm64.",
            "Memory: 32 GB.",
            "Power: Plugged in, battery 80%, energy saver off.",
            "AI helper: Version 0.1.0, protocol 1.",
            "Package: PhotoFinder_0.1.0.600_arm64__abc.",
            "Windows App Runtime: Microsoft.WindowsAppRuntime.1.8 8000.994.2142.0.",
            "Windows AI packages: MicrosoftWindows.Client.CoreAI 1000.26100.9616.0; WindowsWorkload.EP.Qualcomm.QNN.1.8 1.8.41.0.",
            "Text recognition: Ready.",
            "Phi Silica: Not downloaded yet.",
            "Descriptions this session: 12 descriptions, typically 3.1 seconds, slowest 9.8 seconds.",
            "Recent failure: 6:30 PM internal_error: Windows couldn't describe this picture.",
            "AI description check: Worked in 9.8 seconds, and mentions what the sample shows.",
        ], check.Items.Select(i => i.Line));
        // The details are information, not problems, and a bug report keeps them (none names a person or a place).
        Assert.Contains("Windows AI packages: MicrosoftWindows.Client.CoreAI 1000.26100.9616.0;", check.ReportText);
    }

    [Fact]
    public void ABugReport_WritesTheUsersFolderAsUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var check = new SystemCheck(Facts() with { Readiness = new AIReadiness(false, "Error", $@"The helper didn't start: {profile}\x.exe wasn't found.") });
        Assert.Contains(profile, check.Text);
        Assert.DoesNotContain(profile, check.ReportText);
        Assert.Contains(@"%USERPROFILE%\x.exe", check.ReportText);
        Assert.Equal(@"in %USERPROFILE%\a", Privacy.Redact($@"in {profile}\a"));
    }

    [Theory]
    [InlineData("Ready", "Ready")]
    [InlineData("NotReady", "Not downloaded yet")]
    [InlineData("DisabledByUser", "Turned off in Settings")]
    [InlineData("NotSupportedOnCurrentSystem", "Not supported on this PC")]
    [InlineData("Unavailable (COMException, 0x80004005)", "Unavailable (COMException, 0x80004005)")]
    public void ReadyStates_AreSaidInWords(string state, string words) => Assert.Equal(words, SystemCheck.StateText(state));

    [Theory]
    [InlineData("Available", "Permission granted")]
    [InlineData("AvailableWithoutToken", "Permission granted")]
    [InlineData("NoToken", "This copy was built without Microsoft's permission to use it")]
    [InlineData("Unavailable", "Windows didn't grant this copy permission to use it")]
    [InlineData("Unknown", "Permission: Unknown")]
    public void PhiSilicasPermission_IsSaidInWords(string access, string words) => Assert.Equal(words, SystemCheck.AccessText(access));

    [Fact]
    public void PhiSilica_SaysWhetherThisCopyMayUseIt_WhenTheHelperSays()
    {
        var readiness = Ready with
        {
            Components = new Dictionary<string, string> { ["languageModelAccess"] = "Available", ["languageModel"] = "Ready" },
        };
        var check = new SystemCheck(Facts() with { Readiness = readiness });
        Assert.Contains("Phi Silica: Ready. Permission granted.", check.Items.Select(i => i.Line));
    }

    [Fact]
    public void TheHelpersCheck_CarriesItsVersionProtocolAndTheOtherFeatures()
    {
        var r = WindowsAI.ParseReadiness("""{"protocol":1,"version":"0.1.0","identity":true,"state":"Ready","components":{"textRecognition":"Ready","languageModel":"NotSupportedOnCurrentSystem"}}""");
        Assert.Equal("0.1.0", r.HelperVersion);
        Assert.Equal(1, r.Protocol);
        Assert.Equal("NotSupportedOnCurrentSystem", r.Components["languageModel"]);
        // An older helper says neither.
        var old = WindowsAI.ParseReadiness("""{"identity":true,"state":"Ready"}""");
        Assert.Null(old.HelperVersion);
        Assert.Null(old.Protocol);
        Assert.Empty(old.Components);
    }

    [Fact]
    public void Details_AreReadOnThisPC_OrLeftOut()
    {
        // Whatever this PC is, these don't throw; Windows' edition and the memory are always there to read.
        Assert.Contains("build", SystemDetails.WindowsEdition());
        Assert.EndsWith(" GB", SystemDetails.Memory());
        Assert.NotNull(SystemDetails.Processor());
        _ = SystemDetails.Power();
        _ = SystemDetails.AIPackages();
        // Not packaged in a test run.
        Assert.Null(SystemDetails.PackageName());
        Assert.Null(SystemDetails.AppRuntime());
    }

    [Fact]
    public async Task TheNpusDriver_IsReadWhereverThereIsAnNpu()
    {
        if (await SystemCheck.FindNpuAsync() is null) return;
        Assert.StartsWith("version ", await SystemDetails.NpuDriverAsync());
    }

    [Fact]
    public void TheNpuDriver_AndTheAIPackages_AreSaidPlainly()
    {
        // Windows stores the date as midnight UTC, which is the evening before in the Americas: the driver's own date.
        Assert.Equal("version 30.0.220.3000 from Qualcomm Technologies, Inc., dated 1/8/2026",
            SystemDetails.DriverText("30.0.220.3000", "Qualcomm Technologies, Inc.", new DateTimeOffset(2026, 1, 7, 18, 0, 0, TimeSpan.FromHours(-6))));
        Assert.Equal("version 1.2", SystemDetails.DriverText("1.2", null, null));
        Assert.Null(SystemDetails.DriverText(null, "Someone", null));

        Assert.True(SystemDetails.IsAIPackage("MicrosoftWindows.Client.CoreAI"));
        Assert.True(SystemDetails.IsAIPackage("WindowsWorkload.EP.Intel.OpenVINO.1"));
        Assert.True(SystemDetails.IsAIPackage("WindowsWorkload.TextRecognition.Qnn.1"));
        Assert.False(SystemDetails.IsAIPackage("WindowsWorkload.PSTokenizer.Qnn.2.7"));
        Assert.False(SystemDetails.IsAIPackage("Microsoft.Paint"));
        Assert.Equal(["MicrosoftWindows.Client.AIX", "WindowsWorkload.EP.Qualcomm.QNN.1.8", "WindowsWorkload.Manager.1"],
            SystemDetails.Order([("WindowsWorkload.Manager.1", "1"), ("MicrosoftWindows.Client.AIX", "1"), ("WindowsWorkload.EP.Qualcomm.QNN.1.8", "1")]).Select(p => p.Name));
    }

    [Fact]
    public void DescribeStats_SaysHowManyAndHowLong_AndTheLastFewFailures_NewestFirst()
    {
        var stats = new DescribeStats();
        Assert.Null(stats.Timings());
        stats.Failed("timeout", "Too long.", new DateTime(2026, 10, 12, 9, 0, 0));
        Assert.Equal("None succeeded; 1 failure.", stats.Timings());
        foreach (var s in new[] { 2.0, 9.8, 3.0, 4.0 }) stats.Described(TimeSpan.FromSeconds(s));
        Assert.Equal("4 descriptions, typically 3.5 seconds, slowest 9.8 seconds; 1 failure.", stats.Timings());
        stats.Described(TimeSpan.FromSeconds(1));
        Assert.Equal("5 descriptions, typically 3.0 seconds, slowest 9.8 seconds; 1 failure.", stats.Timings());
        for (var i = 1; i <= 4; i++) stats.Failed($"code{i}", $"Failure {i}", new DateTime(2026, 10, 12, 10, i, 0));
        Assert.Equal(["10:04 AM code4: Failure 4.", "10:03 AM code3: Failure 3.", "10:02 AM code2: Failure 2."], stats.RecentFailures());
    }

    [Fact]
    public async Task APhoto_IsCountedOnce_HoweverManySizesWereTried()
    {
        var stats = new DescribeStats();
        var attempts = 0;
        var text = await WindowsAI.CountedAsync(stats, () => WindowsAI.WithRetriesAsync(_ => Task.FromResult(new byte[1]),
            _ => ++attempts == 1 ? throw new DescriptionException("internal_error", "No.") : Task.FromResult(("Fine", TimeSpan.FromSeconds(2)))),
            default, default);
        Assert.Equal("Fine", text);
        Assert.Equal(2, attempts);
        Assert.Equal("1 description, typically 2.0 seconds, slowest 2.0 seconds.", stats.Timings());
        Assert.Empty(stats.RecentFailures());

        // Failing at every size is one failure, not three.
        await Assert.ThrowsAsync<DescriptionException>(() => WindowsAI.CountedAsync(stats,
            () => WindowsAI.WithRetriesAsync<(string, TimeSpan)>(_ => Task.FromResult(new byte[1]), _ => throw new DescriptionException("internal_error", "Never.")),
            default, default));
        Assert.Single(stats.RecentFailures());

        // Without retrying, it's sent once.
        attempts = 0;
        await Assert.ThrowsAsync<DescriptionException>(() => WindowsAI.WithRetriesAsync<string>(_ => Task.FromResult(new byte[1]),
            _ => { attempts++; throw new DescriptionException("internal_error", "No."); }, retry: false));
        Assert.Equal(1, attempts);

        // Something other than the helper's answer is counted by its type.
        await Assert.ThrowsAsync<IOException>(() => WindowsAI.CountedAsync(stats, () => throw new IOException("Gone."), default, default));
        Assert.EndsWith(" IOException: Unexpected; the log has the details.", stats.RecentFailures()[0]);
        Assert.DoesNotContain("Gone", stats.RecentFailures()[0]);
    }

    // MARK: Check AI Description (#95)

    [Fact]
    public void TheSample_ShipsWithTheApp_AsAJpeg()
    {
        var jpeg = AIDescriptionCheck.SampleJpeg();
        Assert.True(jpeg.Length > 10_000);
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
    }

    [Fact]
    public async Task ADescriptionThatMentionsTheSample_LooksRight()
    {
        byte[]? sent = null;
        string? kindAsked = null;
        var result = await AIDescriptionCheck.RunAsync(async (jpegAtSize, kind, _) =>
        {
            sent = await jpegAtSize(512);
            kindAsked = kind;
            return "A red mug sits on a wooden table next to a chalkboard sign that reads \"OPEN.\"";
        }, "brief", Ready, default);
        Assert.Equal(AIDescriptionCheck.SampleJpeg(), sent);
        Assert.Equal("brief", kindAsked);
        Assert.True(result.Worked);
        Assert.StartsWith("Windows AI described the sample photo in ", result.Summary);
        var lines = result.Items.Select(i => i.Line).ToList();
        Assert.Equal("Description: A red mug sits on a wooden table next to a chalkboard sign that reads \"OPEN.\"", lines[0]);
        Assert.Equal("Looks right: OK. It mentions the mug, the table and the OPEN sign.", lines[1]);
        Assert.Contains("Description type: Brief. Change it in the Photo menu.", lines);
        Assert.Contains("AI helper: Version 0.1.0, protocol 1.", lines);
        Assert.Contains(lines, l => l.StartsWith("Sample photo: A red mug on a wooden table beside a sign reading OPEN."));
        Assert.EndsWith(", and mentions what the sample shows.", result.ReportSummary);
        Assert.DoesNotContain(result.Items, i => i.Status == SystemCheck.Status.Problem);
    }

    [Fact]
    public async Task ADescriptionThatMissesSomething_SaysSo_ButIsntAProblem()
    {
        var result = await AIDescriptionCheck.RunAsync((_, _, _) => Task.FromResult("A cup of coffee beside a reopened door."), "accessible", Ready, default);
        Assert.Equal(["the table", "the OPEN sign"], result.Missing);
        Assert.Equal("Looks right: It doesn't mention the table and the OPEN sign, which the sample shows. Descriptions vary, so this isn't necessarily a problem.",
            result.Items[1].Line);
        Assert.DoesNotContain(result.Items, i => i.Status == SystemCheck.Status.Problem);
        Assert.EndsWith(", but doesn't mention the table and the OPEN sign.", result.ReportSummary);
    }

    [Fact]
    public async Task AFailure_IsTheResult_WithItsCode()
    {
        var result = await AIDescriptionCheck.RunAsync((_, _, _) => throw new DescriptionException("content_filtered", "Windows held back this description."),
            "accessible", Ready, default);
        Assert.False(result.Worked);
        // The reason is the first line, where focus lands; the summary doesn't say it again.
        Assert.Equal("Windows AI couldn't describe the sample photo.", result.Summary);
        Assert.Equal("Result: Needs attention. Windows held back this description. (content_filtered)", result.Items[0].Line);
        Assert.Equal("Failed: content_filtered: Windows held back this description.", result.ReportSummary);
    }

    [Fact]
    public async Task SomethingUnexpected_IsShown_ButABugReportGetsOnlyItsType()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var odd = await AIDescriptionCheck.RunAsync((_, _, _) => throw new System.ComponentModel.Win32Exception($"Couldn't start {profile}\\helper.exe"),
            "accessible", Ready, default);
        Assert.Equal($"Result: Needs attention. Couldn't start {profile}\\helper.exe. (Win32Exception)", odd.Items[0].Line);
        Assert.Equal("Failed: Win32Exception.", odd.ReportSummary);
        Assert.DoesNotContain(profile, odd.ReportText);
        Assert.Contains("Result: Needs attention. (Win32Exception)", odd.ReportText);
    }

    [Theory]
    [InlineData("NotInstalled", false, "not_installed message", "not_installed message")]
    [InlineData("NotSupportedOnCurrentSystem", false, null, "Windows' image description needs a Copilot+ PC")]
    [InlineData("NotReady", true, null, "Windows' image description model isn't downloaded yet. Describe Photo (Ctrl+D) downloads it")]
    public async Task WhenWindowsAICantDescribe_OrWouldDownloadFirst_ItSaysSo_WithoutAsking(string state, bool installed, string? message, string expected)
    {
        var asked = false;
        var result = await AIDescriptionCheck.RunAsync((_, _, _) => { asked = true; return Task.FromResult("x"); }, "accessible",
            new AIReadiness(installed, state, message), default);
        Assert.False(asked);
        Assert.False(result.Worked);
        Assert.StartsWith($"Result: Needs attention. {expected}", result.Items[0].Line);
        Assert.EndsWith($"({state})", result.Items[0].Line);
        Assert.DoesNotContain(result.Items, i => i.Name == "Time");
    }

    [Fact]
    public async Task Cancelling_Throws_RatherThanReportingAFailure()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AIDescriptionCheck.RunAsync((_, _, c) => Task.FromCanceled<string>(c), "accessible", null, cancel.Token));
    }

    [WpfFact]
    public async Task TheMenuCommand_ShowsTheDescriptionFirst_AndCheckSystemIncludesTheResult_WithoutSavingAnything()
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        using var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1)) { Descriptions = store };
        await model.StartAsync();
        var window = new MainWindow(model) { ShowActivated = false };
        window.CheckAI = _ => Task.FromResult(Ready);
        window.DescribeSample = (_, _, _) => Task.FromResult("A red mug on a wooden table beside an OPEN sign.");
        window.Show();
        try
        {
            var before = await window.RunSystemCheckAsync();
            Assert.DoesNotContain(before.Items, i => i.Name == "AI description check");

            var check = window.CreateAIDescriptionWindow();
            check.ShowActivated = false;
            check.Show();
            try
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (check.Result is null && DateTime.UtcNow < until) await Task.Delay(20);
                Assert.Equal("Check AI Description", check.Title);
                Assert.Equal("Redescri_be", check.AgainButton.Content);
                Assert.StartsWith("Windows AI described the sample photo in ", check.SummaryText.Text);
                // Focus lands on the description, so it's what's read; the summary follows.
                Assert.Equal(0, check.ChecksList.SelectedIndex);
                Assert.Equal("Description: A red mug on a wooden table beside an OPEN sign.", check.ChecksList.SelectedItem.ToString());
                Assert.Contains(_announced, a => a.StartsWith("Windows AI described the sample photo in "));
                // It says it's working as it starts, since describing takes a while.
                Assert.Contains("Describing the sample photo", _announced);
                Assert.Equal("Result", System.Windows.Automation.AutomationProperties.GetName(check.ChecksList));

                // Describe Again reads the new description too, wherever focus was.
                window.DescribeSample = (_, _, _) => Task.FromResult("A coffee cup on a table by a sign saying OPEN.");
                check.AgainButton.Focus();
                await check.RefreshAsync();
                Assert.Equal(0, check.ChecksList.SelectedIndex);
                Assert.True(check.ChecksList.IsKeyboardFocusWithin);
                Assert.Equal("Description: A coffee cup on a table by a sign saying OPEN.", check.ChecksList.SelectedItem.ToString());
            }
            finally
            {
                check.Close();
            }

            var after = await window.RunSystemCheckAsync();
            Assert.Contains(after.Items, i => i.Line.StartsWith("AI description check: Worked in ") && i.Line.EndsWith("mentions what the sample shows."));
            Assert.Equal(0, store.PhotoCount);
        }
        finally
        {
            window.Close();
        }
    }
}
