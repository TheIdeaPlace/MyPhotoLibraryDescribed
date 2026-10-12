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
        // Something unexpected in an event handler is reported, not a silent crash.
        DispatcherUnhandledException += (_, args) =>
        {
            // Over the window in front (Get Info, say), so focus returns there.
            var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow;
            MessageBox.Show(owner, $"Something went wrong: {args.Exception.Message}", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var settings = Settings.Load(Settings.DefaultPath);
        var descriptions = new DescriptionStore(DescriptionStore.DefaultPath);
        var model = new LibraryModel(settings, Settings.DefaultPath, new MetadataCache(MetadataCache.DefaultPath),
            LibraryRoots.PicturesLibraryAsync) { Descriptions = descriptions };
        var autoDescriber = new AutoDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync,
            describeAhead: DescribeAheadAsync) { MayDescribeAhead = () => !EnergySaverOn() };
        var backgroundDescriber = new BackgroundDescriber(model, descriptions, DescribeAsync, Services.WindowsAI.CheckAsync, Services.WindowsAI.PrepareAsync);
        backgroundDescriber.ForegroundBusy = () => autoDescriber.IsBusy;
        MainWindow = new MainWindow(model) { AutoDescriber = autoDescriber, BackgroundDescriber = backgroundDescriber };
        MainWindow.Show();
    }

    private static Task<string> DescribeAsync(Models.MediaItem item, string kind, CancellationToken cancel) =>
        WindowsAI.DescribeAsync(async size =>
        {
            using var bitmap = await ImageDecoding.DecodeAsync(item.Path, size);
            return await ImageDecoding.ToJpegAsync(bitmap);
        }, kind, cancel);

    /// <summary>Describes a photo ahead (#64): <paramref name="drop"/> gives it up while it's being decoded or waiting its
    /// turn, but not once Windows has it.</summary>
    private static Task<string> DescribeAheadAsync(Models.MediaItem item, string kind, CancellationToken drop) =>
        WindowsAI.DescribeAsync(async size =>
        {
            drop.ThrowIfCancellationRequested();
            using var bitmap = await ImageDecoding.DecodeAsync(item.Path, size);
            drop.ThrowIfCancellationRequested();
            return await ImageDecoding.ToJpegAsync(bitmap);
        }, kind, drop: drop);

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
