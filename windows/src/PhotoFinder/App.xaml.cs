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
            MessageBox.Show(MainWindow, $"Something went wrong: {args.Exception.Message}", "Photo Finder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var settings = Settings.Load(Settings.DefaultPath);
        var descriptions = new DescriptionStore(DescriptionStore.DefaultPath);
        var model = new LibraryModel(settings, Settings.DefaultPath, new MetadataCache(MetadataCache.DefaultPath),
            LibraryRoots.PicturesLibraryAsync) { Descriptions = descriptions };
        var autoDescriber = new AutoDescriber(model, descriptions, DescribeAsync, ReadyAsync);
        MainWindow = new MainWindow(model) { AutoDescriber = autoDescriber };
        MainWindow.Show();
    }

    private static Task<string> DescribeAsync(Models.MediaItem item, string kind, CancellationToken cancel) =>
        WindowsAI.DescribeAsync(async size =>
        {
            using var bitmap = await ImageDecoding.DecodeAsync(item.Path, size);
            return await ImageDecoding.ToJpegAsync(bitmap);
        }, kind, cancel);

    /// <summary>Whether descriptions can be made, getting the model ready (a download, the first time) if Windows needs to.</summary>
    private static async Task<AIReadiness> ReadyAsync(CancellationToken cancel)
    {
        var readiness = await Services.WindowsAI.CheckAsync(cancel);
        if (readiness.State != "NotReady") return readiness;
        Announcer.Announce("Getting Windows' image description model ready. The first time, this can take a few minutes.");
        return await Services.WindowsAI.PrepareAsync(cancel);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowsAI?.Dispose();
        base.OnExit(e);
    }
}
