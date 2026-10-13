using System.Globalization;

namespace PhotoFinder.Services;

/// <summary>How describing has gone this session, for Check System (#94): how many descriptions Windows AI made and how
/// long they took, and the last few failures with their codes. Kept in memory only.</summary>
public sealed class DescribeStats
{
    public static DescribeStats Session { get; } = new();

    private const int Kept = 200;
    private const int FailuresKept = 3;
    private readonly object _gate = new();
    private readonly Queue<double> _seconds = new();
    private readonly Queue<(DateTime When, string Code, string Message)> _failures = new();
    private int _count;
    private int _failed;

    public void Described(TimeSpan took)
    {
        lock (_gate)
        {
            _count++;
            _seconds.Enqueue(took.TotalSeconds);
            if (_seconds.Count > Kept) _seconds.Dequeue();
        }
    }

    public void Failed(string code, string message, DateTime? when = null)
    {
        lock (_gate)
        {
            _failed++;
            _failures.Enqueue((when ?? DateTime.Now, code, message));
            if (_failures.Count > FailuresKept) _failures.Dequeue();
        }
    }

    /// <summary>"12 descriptions; typically 3.1 seconds, slowest 9.8", or null before the first.</summary>
    public string? Timings()
    {
        lock (_gate)
        {
            if (_count == 0) return _failed == 0 ? null : $"None succeeded; {Count(_failed, "failure")}.";
            var sorted = _seconds.Order().ToList();
            var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            var text = $"{Count(_count, "description")}, typically {Seconds(median)}, slowest {Seconds(sorted[^1])}";
            return text + (_failed > 0 ? $"; {Count(_failed, "failure")}." : ".");
        }
    }

    /// <summary>The last few failures, newest first: "18:30 internal_error: Windows couldn't describe this picture".</summary>
    public IReadOnlyList<string> RecentFailures()
    {
        lock (_gate)
            return _failures.Reverse().Select(f => $"{f.When.ToString("t", CultureInfo.CurrentCulture)} {f.Code}: {f.Message.TrimEnd('.')}.").ToList();
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} {what}s";

    private static string Seconds(double s) => $"{s.ToString("0.0", CultureInfo.CurrentCulture)} seconds";
}
