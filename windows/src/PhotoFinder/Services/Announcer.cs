using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace PhotoFinder.Services;

/// <summary>Tells screen readers the results of things that don't move focus: navigation, exports,
/// copies, favorites. A UIA notification event says exactly what to speak, without a visible element
/// to hold the text, and both JAWS and NVDA speak it from desktop apps.</summary>
public static class Announcer
{
    /// <summary>For tests: every announcement, whether or not a window is there to raise it from.</summary>
    internal static Action<string>? Observer { get; set; }

    public static void Announce(string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Observer?.Invoke(text);
        var element = Application.Current?.MainWindow;
        if (element is null) return;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        try
        {
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                text, "PhotoFinderAnnouncement");
        }
        catch (Exception)
        {
            // An announcement isn't worth failing anything over.
        }
    }
}
