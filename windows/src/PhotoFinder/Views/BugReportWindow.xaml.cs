using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>
/// Help > Report a Bug… (#138), as on the Mac (#137): Kind (Bug or Suggestion), Title (focused first), the questions
/// for that kind, an optional Contact field said to be public, "Include the system check" (on), anything that came
/// with the report, and a preview of exactly what's sent. Send (not the default button) files it through the relay;
/// the window then says it was filed, with Open Issue, or that it wasn't, with Open in Browser and the report on the
/// clipboard. Nothing is posted until the person sends it, or submits it on GitHub.
/// </summary>
public partial class BugReportWindow : Window
{
    /// <summary>The Mac's words (#137).</summary>
    public const string ContactNoteText = "Optional, for example an email address. Everything in a report is public on GitHub, including this.";

    private readonly BugReport _report;
    private readonly string? _key;
    private readonly BugReportRelay.Transport? _transport;
    private Uri? _issue;
    private bool _sending;
    private bool _copied;

    /// <param name="key">The relay key, or null to go straight to the browser page.</param>
    /// <param name="transport">Stands in for the network in tests.</param>
    public BugReportWindow(BugReport report, string? key, BugReportRelay.Transport? transport = null)
    {
        _report = report;
        _key = key;
        _transport = transport;
        InitializeComponent();
        ContactNote.Text = ContactNoteText;
        AutomationProperties.SetHelpText(ContactBox, ContactNoteText);
        if (report.Attachments.Count > 0)
        {
            AttachmentsBox.Content = report.AttachmentsTitle;
            AttachmentsBox.IsChecked = report.IncludeAttachments;
            AttachmentsBox.Visibility = Visibility.Visible;
        }
        SystemCheckBox.IsChecked = report.IncludeSystemCheck;
        if (string.IsNullOrEmpty(key))
        {
            // Said with Send too, where it matters, since the note itself isn't a Tab stop.
            NoKeyText.Text = NoKeyNote;
            NoKeyText.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(SendButton, NoKeyNote);
        }
        Read();
        Loaded += (_, _) => TitleBox.Focus();
        // Closing doesn't stop a report already on its way, so it waits for the answer (15 seconds at most).
        Closing += (_, e) =>
        {
            if (!_sending) return;
            e.Cancel = true;
            Announcer.Announce("Still sending");
        };
    }

    public static string NoKeyNote =>
        $"This copy of {AppInfo.Name} can't send reports itself, so Send copies the report and offers to open it on GitHub, filled in, to submit there.";

    /// <summary>For tests: the report as the form has it.</summary>
    internal BugReport Report => _report;

    /// <summary>The form into the report, and the preview.</summary>
    private void Read()
    {
        if (!IsInitialized || PreviewBox is null) return;
        _report.Kind = SuggestionKind.IsChecked == true ? BugReport.Kinds.Suggestion : BugReport.Kinds.Bug;
        _report.Title = TitleBox.Text;
        _report.Happened = HappenedBox.Text;
        _report.Expected = ExpectedBox.Text;
        _report.Steps = StepsBox.Text;
        _report.Suggestion = SuggestionBox.Text;
        _report.Contact = ContactBox.Text;
        _report.IncludeSystemCheck = SystemCheckBox.IsChecked == true;
        _report.IncludeAttachments = AttachmentsBox.IsChecked == true;
        BugFields.Visibility = _report.Kind == BugReport.Kinds.Bug ? Visibility.Visible : Visibility.Collapsed;
        SuggestionFields.Visibility = _report.Kind == BugReport.Kinds.Suggestion ? Visibility.Visible : Visibility.Collapsed;
        PreviewBox.Text = _report.Preview.Replace("\n", Environment.NewLine);
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => Read();

    private void Kind_Checked(object sender, RoutedEventArgs e) => Read();

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    internal async Task SendAsync()
    {
        if (_sending) { Announcer.Announce("Still sending"); return; }
        Read();
        _sending = true;
        SendingText.Visibility = Visibility.Visible;
        Announcer.Announce("Sending");
        try
        {
            var result = await BugReportRelay.SendAsync(_report, _key, _transport);
            if (result.Filed)
            {
                _issue = result.IssueUrl;
                FiledHeading.Text = $"Report filed as issue #{result.Number}";
                Show(FiledPanel, OpenIssueButton, FiledText.Text);
                return;
            }
            _copied = Copy(_report.ClipboardText);
            NotSentHeading.Text = result.NoKey ? "Ready to submit on GitHub" : "Couldn't send the report";
            NotSentText.Text = _copied
                ? "It's on the clipboard. Open in Browser shows it on GitHub, filled in, to submit there. Nothing is posted until you do."
                : "It couldn't be copied, so it's below to copy yourself. Open in Browser shows it on GitHub, filled in, to submit there. Nothing is posted until you do.";
            if (!_copied)
            {
                ReportTextBox.Text = _report.ClipboardText.Replace("\n", Environment.NewLine);
                ReportTextPanel.Visibility = Visibility.Visible;
            }
            Show(NotSentPanel, OpenInBrowserButton, NotSentText.Text);
        }
        finally
        {
            _sending = false;
            SendingText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The result in place of the form, said where focus lands (its button's help text, as QuestionDialog
    /// does) rather than announced over the focus change, which would cut it off.</summary>
    private void Show(FrameworkElement panel, Button focus, string text)
    {
        var heading = panel == FiledPanel ? FiledHeading.Text : NotSentHeading.Text;
        AutomationProperties.SetHelpText(focus, $"{heading}. {text}");
        FormPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => focus.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            // Another app holding the clipboard.
            return false;
        }
    }

    private void OpenIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_issue is not null) TryOpen(_issue.AbsoluteUri, $"The issue is at {_issue.AbsoluteUri}.");
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var where = _copied ? $"The report is on the clipboard: paste it into a new issue at {AppInfo.IssuesUrl}."
            : $"Copy the report below and paste it into a new issue at {AppInfo.IssuesUrl}.";
        if (TryOpen(_report.IssuePageUri(_copied).AbsoluteUri, where)) Close();
    }

    /// <summary>Opens a page with the app's own opener (MainWindow.OpenUrl, replaced in tests).</summary>
    private bool TryOpen(string url, string otherwise)
    {
        try
        {
            MainWindow.OpenUrl(url);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"The browser couldn't be opened. {otherwise}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
