namespace PhotoFinder.Services;

/// <summary>What the check windows show: Check System (#32) and Check AI Description (#95). A summary heading, one line
/// per item, and everything as text for Copy to Clipboard.</summary>
public interface ICheckResult
{
    string Summary { get; }

    IReadOnlyList<SystemCheck.Item> Items { get; }

    string Text { get; }
}
