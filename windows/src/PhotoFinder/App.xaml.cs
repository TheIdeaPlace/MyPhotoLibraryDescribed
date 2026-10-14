using System.Windows;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder;

public partial class App : Application
{
    private static WindowsAI? _windowsAI;

    /// <summary>One helper process for the whole app, started the first time a photo is described.</summary>
    public static WindowsAI WindowsAI => _windowsAI ??= new WindowsAI();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Whether the last run quit cleanly (#33): offered as a report once the window is up, with the end of the log
        // read now, before this run adds to it.
        UncleanLaunch = CrashCheck.Begin();
        if (UncleanLaunch is not null) CrashAttachments = CrashCheck.Attachments();
        Log.RemoveOld();
        Log.Write("start", $"{AppInfo.Name} {Updates.AppVersion} on Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Crash("crash", "Unhandled", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Crash("error", "A background task failed unseen", args.Exception);
        // Something unexpected in an event handler is reported, not a silent crash.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Crash("error", "Unhandled in the window", args.Exception);
            // Over the window in front (Get Info, say), so focus returns there.
            var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow;
            MessageBox.Show(owner, $"Something went wrong: {args.Exception.Message}", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (UncleanLaunch is not null) Log.Write("start", "The last run didn't quit cleanly");
        var settings = Settings.Load(Settings.DefaultPath);
        // Every launch starts read-only (#83): nothing may change a photo until the person allows it.
        PhotoChanges.Allowed = !settings.ReadOnly;
        var descriptions = new DescriptionStore(DescriptionStore.DefaultPath);
        // What's saved with each description (#199): Windows' package list is slow, so it's read now, off this thread.
        DescriptionProvenance.Warm();
        var model = new LibraryModel(settings, Settings.DefaultPath, new MetadataCache(MetadataCache.DefaultPath),
            LibraryRoots.PicturesLibraryAsync) { Descriptions = descriptions };
        var autoDescriber = new AutoDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync,
            describeAhead: DescribeAheadAsync) { MayDescribeAhead = () => !EnergySaverOn() };
        var backgroundDescriber = new BackgroundDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync,
            BackgroundDescriber.DefaultFailuresPath);
        backgroundDescriber.ForegroundBusy = () => autoDescriber.IsBusy;
        backgroundDescriber.MayRun = () => !EnergySaverOn();
        // Describe Photo working after background describing gave up waiting for Windows AI: it starts, unasked.
        WindowsAI.DescribedOne += () => Dispatcher.BeginInvoke(backgroundDescriber.WindowsAIWorked);
        MainWindow = new MainWindow(model) { AutoDescriber = autoDescriber, BackgroundDescriber = backgroundDescriber };
        MainWindow.Show();
    }

    private static Task<string> DescribeAsync(Models.MediaItem item, string kind, CancellationToken cancel) =>
        WindowsAI.DescribeAsync(JpegOf(item, default), kind, cancel);

    /// <summary>Describes a photo ahead (#64): <paramref name="drop"/> gives it up while it's being decoded or waiting its
    /// turn, but not once Windows has it.</summary>
    private static Task<string> DescribeAheadAsync(Models.MediaItem item, string kind, CancellationToken drop) =>
        WindowsAI.DescribeAsync(JpegOf(item, drop), kind, drop: drop);

    /// <summary>The photo as a JPEG at a given size, for Windows AI; <paramref name="drop"/> stops it before and after decoding.</summary>
    private static Func<uint, Task<byte[]>> JpegOf(Models.MediaItem item, CancellationToken drop) => async size =>
    {
        drop.ThrowIfCancellationRequested();
        using var bitmap = await ImageDecoding.DecodeAsync(item.Path, size);
        drop.ThrowIfCancellationRequested();
        return await ImageDecoding.ToJpegAsync(bitmap);
    };

    /// <summary>Windows' energy saver (battery saver) is on: work ahead waits.</summary>
    private static bool EnergySaverOn()
    {
        try
        {
            return global::Windows.System.Power.PowerManager.EnergySaverStatus == global::Windows.System.Power.EnergySaverStatus.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>When the last run started, if it didn't quit cleanly; cleared once the offer to report it is made.</summary>
    public static DateTime? UncleanLaunch { get; set; }

    /// <summary>The end of the crashed run's log, for the crash report.</summary>
    public static List<BugReport.Attachment> CrashAttachments { get; set; } = [];

    protected override void OnExit(ExitEventArgs e)
    {
        CrashCheck.End();
        _windowsAI?.Dispose();
        base.OnExit(e);
    }
}
