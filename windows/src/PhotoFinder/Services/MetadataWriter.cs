using PhotoFinder.Models;
using Windows.Storage;

namespace PhotoFinder.Services;

/// <summary>Writes captions and ratings into the files themselves, through the same property handlers
/// File Explorer uses, so they travel with the file (and sync with OneDrive).</summary>
public static class MetadataWriter
{
    /// <summary>Sets the caption (System.Title). Empty removes it.</summary>
    public static Task<WriteResult> SetCaptionAsync(IReadOnlyList<MediaItem> items, string caption)
    {
        var text = caption.Trim();
        return WriteAsync(items, "System.Title", text.Length == 0 ? null : text);
    }

    /// <summary>Sets a different caption on each file.</summary>
    public static async Task<WriteResult> SetCaptionsAsync(IReadOnlyList<(MediaItem Item, string Caption)> captions)
    {
        var failures = new List<(MediaItem Item, string Reason)>();
        foreach (var (item, caption) in captions)
            failures.AddRange((await SetCaptionAsync([item], caption)).Failures);
        return new WriteResult(captions.Count - failures.Count, failures);
    }

    /// <summary>Sets the rating, 0 to 5 stars. 0 removes it.</summary>
    public static Task<WriteResult> SetRatingAsync(IReadOnlyList<MediaItem> items, int stars) =>
        WriteAsync(items, "System.Rating", stars <= 0 ? null : MediaTypes.RatingFromStars(stars));

    private static async Task<WriteResult> WriteAsync(IReadOnlyList<MediaItem> items, string key, object? value)
    {
        var failures = new List<(MediaItem Item, string Reason)>();
        foreach (var item in items)
        {
            try
            {
                // Saving properties rewrites the file, which changes its modified date. Put it back, so a
                // photo without a Date Taken doesn't jump to today in the list.
                // UTC, so the hour that repeats when daylight saving ends can't shift it.
                var modified = File.GetLastWriteTimeUtc(item.Path);
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                await file.Properties.SavePropertiesAsync([new KeyValuePair<string, object?>(key, value)]);
                try
                {
                    File.SetLastWriteTimeUtc(item.Path, modified);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Saved, but OneDrive or a virus scanner had the file a moment: it keeps today's date.
                }
                // Some handlers accept a write and store nothing. Read it back, from a fresh handle, before calling it saved.
                var check = await StorageFile.GetFileFromPathAsync(item.Path);
                var stored = await check.Properties.RetrievePropertiesAsync([key]);
                if (!Matches(stored.TryGetValue(key, out var actual) ? actual : null, value))
                    throw new NotSupportedException("The value didn't stick.");
            }
            catch (Exception ex)
            {
                failures.Add((item, Reason(item, ex)));
            }
        }
        return new WriteResult(items.Count - failures.Count, failures);
    }

    /// <summary>Whether what's stored is what was written. Ratings come back as the nearest Windows value,
    /// so they're compared as stars.</summary>
    internal static bool Matches(object? stored, object? written) => written switch
    {
        null => stored is null || (stored is string s && s.Length == 0) || (stored is uint u && u == 0),
        string text => stored is string s && s.Trim() == text,
        uint rating => stored is not null && MediaTypes.StarsFromRating(Convert.ToUInt32(stored, System.Globalization.CultureInfo.InvariantCulture)) == MediaTypes.StarsFromRating(rating),
        _ => Equals(stored, written),
    };

    private static string Reason(MediaItem item, Exception ex) => ex switch
    {
        UnauthorizedAccessException => $"{item.Name} is read-only or in use.",
        FileNotFoundException => $"{item.Name} is no longer there.",
        // E_FAIL and "not implemented" are what a format without a writable property handler gives.
        NotImplementedException or NotSupportedException => $"{item.Name} is a {MediaTypes.FileTypeDescription(item.Path)}, which can't store this.",
        _ when ex.HResult is unchecked((int)0x80004005) or unchecked((int)0x80004001) or unchecked((int)0x8007000D) or unchecked((int)0x80030005)
            => $"{item.Name} is a {MediaTypes.FileTypeDescription(item.Path)}, which can't store this.",
        _ => $"{item.Name}: {ex.Message.Trim()}",
    };
}

public sealed record WriteResult(int Succeeded, IReadOnlyList<(MediaItem Item, string Reason)> Failures)
{
    public string FailureSummary => Failures.Count switch
    {
        0 => "",
        1 => Failures[0].Reason,
        _ => $"{Failures.Count} items couldn't be changed. {Failures[0].Reason}",
    };
}
