using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>Help > Check System…: the checks as a list, one line each, with Check Again, Copy to Clipboard and Report
/// a Bug, as on the Mac (#32).</summary>
public partial class CheckSystemWindow : Window
{
    private readonly Func<CancellationToken, Task<SystemCheck>> _check;
    private readonly Action<SystemCheck?> _reportBug;
    // Closing stops a check still running (the AI helper can take a while), and nothing is said for it afterwards.
    private readonly CancellationTokenSource _closing = new();
    private bool _checking;

    public CheckSystemWindow(Func<CancellationToken, Task<SystemCheck>> check, Action<SystemCheck?> reportBug)
    {
        _check = check;
        _reportBug = reportBug;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync(firstTime: true);
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The latest results, for tests and for Report a Bug.</summary>
    internal SystemCheck? Result { get; private set; }

    internal async Task RefreshAsync(bool firstTime = false)
    {
        if (_checking)
        {
            Announcer.Announce("Still checking");
            return;
        }
        _checking = true;
        // Where focus was in the list: the items are replaced, and focus on one of them would be lost with it.
        var focusedIndex = ChecksList.IsKeyboardFocusWithin ? Math.Max(0, ChecksList.SelectedIndex) : -1;
        try
        {
            SummaryText.Text = "Checking…";
            if (!firstTime) Announcer.Announce("Checking");
            var result = await _check(_closing.Token);
            if (_closing.IsCancellationRequested) return;
            Result = result;
            SummaryText.Text = result.Summary;
            // Read with the list (JAWS Insert+Tab, NVDA's description), since the heading isn't a Tab stop.
            AutomationProperties.SetHelpText(ChecksList, result.Summary);
            ChecksList.ItemsSource = result.Items;
            if (firstTime || focusedIndex >= 0)
            {
                var index = Math.Min(Math.Max(focusedIndex, 0), result.Items.Count - 1);
                ChecksList.SelectedIndex = index;
                ChecksList.UpdateLayout();
                if (ChecksList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
            }
            // The summary queued after what's said for the check focus lands on, so neither cuts the other off.
            if (firstTime) Announcer.AnnounceAfter(result.Summary, "PhotoFinderCheckSystem");
            else Announcer.Announce(result.Summary);
        }
        catch (OperationCanceledException)
        {
            // Closed while checking.
        }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested) return;
            SummaryText.Text = $"The check couldn't finish: {ex.Message}";
            Announcer.Announce(SummaryText.Text);
        }
        finally
        {
            _checking = false;
        }
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Result is null)
        {
            Announcer.Announce("Still checking");
            return;
        }
        try
        {
            Clipboard.SetText(Result.Text);
            Announcer.Announce("Copied");
        }
        catch (ExternalException)
        {
            // Another app can hold the clipboard open for a moment.
            Announcer.Announce("The clipboard is busy. Try again.");
        }
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e) => _reportBug(Result);
}
