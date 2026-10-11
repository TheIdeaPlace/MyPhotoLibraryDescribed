using System.Windows;
using System.Windows.Automation;

namespace PhotoFinder.Views;

public partial class CaptionDialog : Window
{
    public CaptionDialog(int count, string initialText)
    {
        InitializeComponent();
        Title = count == 1 ? "Edit Caption" : $"Edit Caption for {count} Items";
        CaptionBox.Text = initialText;
        if (count > 1)
        {
            ManyNote.Text = $"This replaces the caption on all {count} items.";
            ManyNote.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(CaptionBox, ManyNote.Text);
        }
        Loaded += (_, _) =>
        {
            CaptionBox.Focus();
            CaptionBox.SelectAll();
        };
    }

    public string Caption => CaptionBox.Text;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
