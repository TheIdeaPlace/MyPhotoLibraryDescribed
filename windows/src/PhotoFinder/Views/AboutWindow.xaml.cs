using System.Windows;

namespace PhotoFinder.Views;

/// <summary>Help > About (#33, #138), as on the Mac: the name, the version, what it's for, and links to the source code,
/// the user guide and the privacy policy.</summary>
public partial class AboutWindow : Window
{
    /// <summary>Description of the app, said and shown.</summary>
    public const string What = "Describes your photos on your own PC, for people using screen readers.";

    private readonly Func<string, string, bool> _open;

    /// <param name="open">Opens a link, given what it is ("The user guide is"), and says where it is if it can't.</param>
    public AboutWindow(Func<string, string, bool> open)
    {
        _open = open;
        InitializeComponent();
        Title = $"About {AppInfo.Name}";
        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {Services.Updates.AppVersion}";
        WhatText.Text = $"{What} Place names {Services.PlaceNames.Credit}.";
        // Focus starts on Close, so Enter dismisses it as an About box should, and it says what the window shows.
        System.Windows.Automation.AutomationProperties.SetHelpText(CloseButton, $"{AppInfo.Name}, version {Services.Updates.AppVersion}. {What}");
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void Source_Click(object sender, RoutedEventArgs e) => _open($"https://github.com/{AppInfo.Repository}", "The source code is");
    private void Guide_Click(object sender, RoutedEventArgs e) => _open(AppInfo.UserGuideUrl, "The user guide is");
    private void Privacy_Click(object sender, RoutedEventArgs e) => _open(AppInfo.PrivacyPolicyUrl, "The privacy policy is");
}
