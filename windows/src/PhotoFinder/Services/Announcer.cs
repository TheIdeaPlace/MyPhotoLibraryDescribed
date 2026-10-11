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

    public static void Announce(string text, bool interrupt = false) =>
        Raise(text, interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            "PhotoFinderAnnouncement");

    /// <summary>Speaks after whatever is being said, instead of cutting it off (NVDA cancels speech for
    /// MostRecent notifications). For photo descriptions, which mustn't cut off the row's own name; moving
    /// on still silences them, as any focus change does.</summary>
    public static void AnnounceAfter(string text, string activityId) => Raise(text, AutomationNotificationProcessing.All, activityId);

    private static void Raise(string text, AutomationNotificationProcessing processing, string activityId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Observer?.Invoke(text);
        // From the window in front (Get Info, Preview or the main window): screen readers can ignore
        // notifications from a window that isn't the active one.
        var element = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (element is null) return;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        try
        {
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, processing, text, activityId);
        }
        catch (Exception)
        {
            // An announcement isn't worth failing anything over.
        }
    }
}
