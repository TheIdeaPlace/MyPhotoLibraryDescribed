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
        var settings = Settings.Load(Settings.DefaultPath);
        // Every launch starts read-only (#83): nothing may change a photo until the person allows it.
        PhotoChanges.Allowed = !settings.ReadOnly;
        var descriptions = new DescriptionStore(DescriptionStore.DefaultPath);
        var model = new LibraryModel(settings, Settings.DefaultPath, new MetadataCache(MetadataCache.DefaultPath),
            LibraryRoots.PicturesLibraryAsync) { Descriptions = descriptions };
        var autoDescriber = new AutoDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync,
            describeAhead: DescribeAheadAsync) { MayDescribeAhead = () => !EnergySaverOn() };
        var backgroundDescriber = new BackgroundDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync);
        backgroundDescriber.ForegroundBusy = () => autoDescriber.IsBusy;
        backgroundDescriber.MayRun = () => !EnergySaverOn();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _windowsAI?.Dispose();
        base.OnExit(e);
    }
}
