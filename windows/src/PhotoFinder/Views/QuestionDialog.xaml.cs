using System.Windows;
using System.Windows.Automation;

namespace PhotoFinder.Views;

/// <summary>A yes-or-cancel question whose yes button says what it does ("Save in Photos").</summary>
public partial class QuestionDialog : Window
{
    /// <param name="keep">For a risky yes: the safe button's name ("Keep Read-Only Mode"). It's then the default and has focus,
    /// so Enter, or a key held down, never says yes.</param>
    public QuestionDialog(string title, string question, string details, string yes, string? keep = null)
    {
        InitializeComponent();
        Title = title;
        QuestionText.Text = question;
        DetailsText.Text = details;
        YesButton.Content = yes;
        var first = YesButton;
        if (keep is not null)
        {
            CancelButton.Content = keep;
            YesButton.IsDefault = false;
            CancelButton.IsDefault = true;
            first = CancelButton;
        }
        AutomationProperties.SetHelpText(first, $"{question} {details}");
        Loaded += (_, _) => first.Focus();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Asks, over <paramref name="owner"/>. True for the yes button. Replaceable in tests.</summary>
    internal static Func<Window, string, string, string, string, bool> Ask { get; set; } =
        (owner, title, question, details, yes) => new QuestionDialog(title, question, details, yes) { Owner = owner }.ShowDialog() == true;

    /// <summary>Asks where the other answer (<c>keep</c>) is the default and has focus, as for a risky yes or a dialog that
    /// mostly just closes: Enter and Escape both give it. True for yes. Replaceable in tests.</summary>
    internal static Func<Window, string, string, string, string, string, bool> AskSafely { get; set; } =
        (owner, title, question, details, yes, keep) => new QuestionDialog(title, question, details, yes, keep) { Owner = owner }.ShowDialog() == true;
}
