namespace PhotoFinder.Services;

/// <summary>
/// A plain-text diagnostic log, one file a day in the data folder's Logs folder, kept for two weeks: what Photo Finder
/// started with, and what went wrong (the AI helper, describing, saving, crashes). For finding out what happened when
/// something goes wrong; it stays on this PC unless the person sends it. Help > Open Log Folder shows it. Never throws.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Two weeks of daily files are kept.</summary>
    internal const int DaysKept = 14;

    public static string Folder => Path.Combine(AppPaths.DataFolder, "Logs");

    /// <summary>Today's file.</summary>
    public static string TodaysFile => Path.Combine(Folder, $"PhotoFinder-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Writes one line: the time, the area ("describe", "background", "save", "helper") and what happened.</summary>
    public static void Write(string area, string message)
    {
        // So a log can be attached to a public bug report.
        message = Privacy.Redact(message);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message.Replace("\r", " ").Replace("\n", " ").Trim()}";
        System.Diagnostics.Trace.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(TodaysFile, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // A log that can't be written mustn't get in the way, whatever the reason.
        }
    }

    /// <summary>An exception, with its type and HRESULT, and where it was thrown.</summary>
    public static void Error(string area, string what, Exception ex) =>
        Write(area, $"{what}: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message} | {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");

    /// <summary>A crash or an unhandled exception: everything, inner exceptions and the whole stack included.</summary>
    public static void Crash(string area, string what, Exception ex) => Write(area, $"{what}: {ex}");

    /// <summary>Removes files older than two weeks. Run once at start.</summary>
    public static void RemoveOld()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Now.Date.AddDays(-DaysKept);
            foreach (var file in Directory.EnumerateFiles(Folder, "PhotoFinder-*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch (Exception)
        {
            // Left for next time.
        }
    }
}
