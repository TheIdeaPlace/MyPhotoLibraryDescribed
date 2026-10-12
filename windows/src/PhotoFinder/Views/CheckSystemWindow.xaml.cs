using System.Windows;
using System.Windows.Controls;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>Help > Check System…: the checks as a list, one line each, with Check Again, Copy to Clipboard and Report
/// a Bug, as on the Mac (#32).</summary>
public partial class CheckSystemWindow : Window
{
    private readonly Func<Task<SystemCheck>> _check;
    private readonly Action<SystemCheck?> _reportBug;
    private bool _checking;

    public CheckSystemWindow(Func<Task<SystemCheck>> check, Action<SystemCheck?> reportBug)
    {
        _check = check;
        _reportBug = reportBug;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync(firstTime: true);
    }

    /// <summary>The latest results, for tests and for Report a Bug.</summary>
    internal SystemCheck? Result { get; private set; }

    internal async Task RefreshAsync(bool firstTime = false)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            SummaryText.Text = "Checking…";
            Result = await _check();
            SummaryText.Text = Result.Summary;
            ChecksList.ItemsSource = Result.Items;
            if (firstTime)
            {
                // Onto the first check, then the summary queued after what's said for it, so neither cuts the other off.
                ChecksList.UpdateLayout();
                if (ChecksList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first) first.Focus();
                Announcer.AnnounceAfter(Result.Summary, "PhotoFinderCheckSystem");
            }
            else
            {
                Announcer.Announce(Result.Summary);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Result is null) return;
        try
        {
            Clipboard.SetText(Result.Text);
            Announcer.Announce("Copied");
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard open for a moment.
            Announcer.Announce($"The clipboard is busy. Try again. {ex.Message}");
        }
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e) => _reportBug(Result);
}
