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
        var model = new LibraryModel(settings, Settings.DefaultPath, new MetadataCache(MetadataCache.DefaultPath),
            LibraryRoots.PicturesLibraryAsync);
        MainWindow = new MainWindow(model);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowsAI?.Dispose();
        base.OnExit(e);
    }
}
