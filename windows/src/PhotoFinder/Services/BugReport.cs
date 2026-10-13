namespace PhotoFinder.Services;

/// <summary>
/// Help > Report a Bug: a new GitHub issue, prefilled with the questions the app kit's Report a Bug form asks and the
/// system check, the same as the Mac's (#33). Until the app kit's relay is set up for this app, it opens in the browser
/// and the same text goes on the clipboard.
/// </summary>
public static class BugReport
{
    /// <summary>Longer addresses than this risk GitHub or the browser refusing them, so the body is left to the clipboard.</summary>
    public const int MaxUriLength = 6000;

    public static string Body(string systemText) => $"""
        **What happened**


        **What you expected**


        **Steps**
        1.

        **System**
        ```
        {systemText}
        ```

        Help > Open Log Folder has a log of what went wrong, which can be attached here. It names files and folders, so look at it first: issues are public.
        """;

    /// <summary>The new-issue page, titled and labelled as from Windows (GitHub applies labels only for people who can
    /// triage the repo, so the title says it too). Without the body when that would make the address too long.</summary>
    public static Uri NewIssueUri(string body, out bool includesBody)
    {
        var start = $"{AppInfo.IssuesUrl}?title={Uri.EscapeDataString("[Windows] ")}&labels={Uri.EscapeDataString("bug,user-reported,windows")}";
        var full = $"{start}&body={Uri.EscapeDataString(body)}";
        includesBody = full.Length <= MaxUriLength;
        return new Uri(includesBody ? full : start);
    }
}
