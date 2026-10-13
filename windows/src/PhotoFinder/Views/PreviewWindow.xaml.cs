using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>A quick look at photos and videos without leaving Photo Finder: Space or Escape closes it,
/// the arrow keys move through the items. Videos play here.</summary>
public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<FileRow> _rows;
    private int _index;
    private bool _playing;
    private int _loadVersion;

    public PreviewWindow(IReadOnlyList<FileRow> rows, int index)
    {
        _rows = rows;
        _index = Math.Clamp(index, 0, Math.Max(0, rows.Count - 1));
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Show(_index, announce: false);
            Picture.Focus();
        };
        Closed += (_, _) => Video.Close();
    }

    /// <summary>The item showing when the window closed.</summary>
    public FileRow? CurrentRow => _rows.Count == 0 ? null : _rows[_index];

    /// <summary>Describe Photo (Ctrl+D), or Redescribe (Ctrl+R) when the flag is set, for the item
    /// shown, given whether you're still on it when it's done.</summary>
    public Func<FileRow, Func<bool>, bool, Task>? Describe { get; init; }

    private async void Show(int index, bool announce)
    {
        if (_rows.Count == 0) return;
        _index = index;
        var row = _rows[index];
        var version = ++_loadVersion;
        Title = $"{row.Name} - Preview";
        CaptionText.Text = row.Caption;
        PositionText.Text = $"{index + 1} of {_rows.Count}";
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index + 1 < _rows.Count;
        AutomationProperties.SetName(Picture, row.SpokenDescription);
        if (announce) Announcer.Announce($"{row.Name}, {index + 1} of {_rows.Count}");

        Video.Stop();
        Video.Source = null;
        Video.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        Picture.Visibility = Visibility.Visible;

        if (row.Item is { IsVideo: true } video)
        {
            Picture.Show(null, "");
            Video.Source = new Uri(video.Path);
            Video.Visibility = Visibility.Visible;
            Video.Play();
            _playing = true;
            PlayButton.Content = "_Pause";
            PlayButton.Visibility = Visibility.Visible;
            return;
        }

        Picture.Show(null, "Loading image…");
        var image = row.Item is { } item ? await ImageDecoding.LoadForDisplayAsync(item.Path, false, 3000) : null;
        if (version != _loadVersion) return;
        Picture.Show(image, "This picture can't be shown.");
        if (image is null) Announcer.Announce("This picture can't be shown.");
    }

    private void Move(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _rows.Count) { System.Media.SystemSounds.Beep.Play(); return; }
        Show(next, announce: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // With Alt down, WPF may report the key as System, with the real one in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Describe Photo (Ctrl+D) and Redescribe (Ctrl+R), as in the main window.
        if (key is Key.D or Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var again = key == Key.R;
            if (CurrentRow is { } row && Describe is not null) _ = Describe(row, () => IsActive && CurrentRow == row, again);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // Buttons keep their own keys, so Tab to Next and Space still presses it.
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && e.Key is Key.Space or Key.Enter) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: Move(-1); break;
            case Key.Right or Key.Down: Move(1); break;
            case Key.Space or Key.Escape: Close(); break;
            case Key.K or Key.P when Video.Visibility == Visibility.Visible: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void TogglePlay()
    {
        if (_playing) Video.Pause(); else Video.Play();
        _playing = !_playing;
        PlayButton.Content = _playing ? "_Pause" : "_Play";
        Announcer.Announce(_playing ? "Playing" : "Paused");
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        PlayButton.Content = "_Play";
        Video.Stop();
    }

    private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Video.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        Picture.Show(null, "This video can't be played here. Press Enter in the list to open it in its app.");
        Announcer.Announce("This video can't be played here. Press Enter in the list to open it in its app.");
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
