using System.Windows;
using System.Windows.Automation;

namespace PhotoFinder.Views;

/// <summary>A yes-or-cancel question whose yes button says what it does ("Save in Photos").</summary>
public partial class QuestionDialog : Window
{
    public QuestionDialog(string title, string question, string details, string yes)
    {
        InitializeComponent();
        Title = title;
        QuestionText.Text = question;
        DetailsText.Text = details;
        YesButton.Content = yes;
        AutomationProperties.SetHelpText(YesButton, $"{question} {details}");
        Loaded += (_, _) => YesButton.Focus();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Asks, over <paramref name="owner"/>. True for the yes button. Replaceable in tests.</summary>
    internal static Func<Window, string, string, string, string, bool> Ask { get; set; } =
        (owner, title, question, details, yes) => new QuestionDialog(title, question, details, yes) { Owner = owner }.ShowDialog() == true;
}
