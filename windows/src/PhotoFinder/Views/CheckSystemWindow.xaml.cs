using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>Help > Check System…: the checks as a list, one line each, with Check Again, Copy to Clipboard and Report
/// a Bug, as on the Mac (#32). Help > Check AI Description… (#95) uses the same window for its result.</summary>
public partial class CheckSystemWindow : Window
{
    private readonly Func<CancellationToken, Task<ICheckResult>> _check;
    private readonly Action<ICheckResult?> _reportBug;
    private readonly string _busyText;
    private readonly bool _readsResult;
    // Closing stops a check still running (the AI helper can take a while), and nothing is said for it afterwards.
    private readonly CancellationTokenSource _closing = new();
    private bool _checking;

    public CheckSystemWindow(Func<CancellationToken, Task<SystemCheck>> check, Action<SystemCheck?> reportBug)
        : this("Check System", "Checking…", "Check _Again", "Checks", readsResult: false, async cancel => await check(cancel), r => reportBug(r as SystemCheck))
    {
    }

    /// <param name="busyText">The heading while it runs ("Checking…").</param>
    /// <param name="againLabel">The button that runs it again, with its access key ("Check _Again").</param>
    /// <param name="listName">The list's name, read with it ("Checks").</param>
    /// <param name="readsResult">A result to hear (a description) rather than a list to look through: it says it's working
    /// as it starts, which takes a while, and focus goes to the first line every time it finishes, so it's read.</param>
    public CheckSystemWindow(string title, string busyText, string againLabel, string listName, bool readsResult,
        Func<CancellationToken, Task<ICheckResult>> check, Action<ICheckResult?> reportBug)
    {
        _readsResult = readsResult;
        _check = check;
        _reportBug = reportBug;
        _busyText = busyText;
        InitializeComponent();
        Title = title;
        SummaryText.Text = busyText;
        AgainButton.Content = againLabel;
        AutomationProperties.SetName(ChecksList, listName);
        Loaded += async (_, _) => await RefreshAsync(firstTime: true);
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The latest results, for tests and for Report a Bug.</summary>
    internal ICheckResult? Result { get; private set; }

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
            SummaryText.Text = _busyText;
            if (!firstTime) Announcer.Announce(_busyText.TrimEnd('…'));
            else if (_readsResult) Announcer.AnnounceAfter(_busyText.TrimEnd('…'), "PhotoFinderCheckBusy");
            var result = await _check(_closing.Token);
            if (_closing.IsCancellationRequested) return;
            Result = result;
            SummaryText.Text = result.Summary;
            // Read with the list (JAWS Insert+Tab, NVDA's description), since the heading isn't a Tab stop.
            AutomationProperties.SetHelpText(ChecksList, result.Summary);
            ChecksList.ItemsSource = result.Items;
            if (firstTime || focusedIndex >= 0 || _readsResult)
            {
                var index = _readsResult ? 0 : Math.Min(Math.Max(focusedIndex, 0), result.Items.Count - 1);
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
