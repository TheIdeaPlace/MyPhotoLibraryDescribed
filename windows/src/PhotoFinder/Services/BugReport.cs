namespace PhotoFinder.Services;

/// <summary>
/// Help > Report a Bug: a new GitHub issue, prefilled with the questions the app kit's Report a Bug form asks and the
/// system check, the same as the Mac's (#33). Until the app kit's relay is set up for this app, it opens in the browser
/// and the same text goes on the clipboard.
/// </summary>
public static class BugReport
{
    public static string Body(string systemText) => $"""
        **What happened**


        **What you expected**


        **Steps**
        1.

        **System**
        ```
        {systemText}
        ```
        """;

    /// <summary>The new-issue page, titled and labelled as from Windows. GitHub applies labels only for people who can
    /// triage the repo, so the title says it's from Windows too.</summary>
    public static Uri NewIssueUri(string body) =>
        new($"{AppInfo.IssuesUrl}?title={Uri.EscapeDataString("[Windows] ")}&body={Uri.EscapeDataString(body)}&labels={Uri.EscapeDataString("bug,user-reported,windows")}");
}
