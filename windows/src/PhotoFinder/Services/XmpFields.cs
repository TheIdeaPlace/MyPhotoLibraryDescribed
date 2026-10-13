using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoFinder.Services;

/// <summary>
/// A photo file's XMP caption (dc:description) and IPTC accessibility fields (#31, #62). JPEG, PNG, TIFF and HEIC, as on
/// the Mac; other formats keep their caption in Explorer's Title (System.Title).
///
/// - JPEG is changed segment by segment (<see cref="JpegMetadata"/>, #86): only the XMP is replaced, so EXIF (with its
///   thumbnail and MakerNote), the picture and anything after it (a motion photo's video) stay byte for byte.
/// - PNG is changed chunk by chunk (<see cref="PngMetadata"/>, #82): only the XMP chunk is replaced.
/// - HEIC is changed box by box (<see cref="HeifMetadata"/>, #31): only the photo's XMP item, added when there's none.
///   Windows' own property handler, used before, rewrote the whole file to save a Title.
/// - TIFF goes through WIC: its in-place encoder when the file has room, otherwise its transcoder, which rewrites the file
///   without decoding the picture.
/// - Every way, the new file is written beside the photo, flushed, and swapped in whole, so a failure partway leaves the
///   photo as it was; and the fields are read back before they count as saved.
/// </summary>
public static class XmpFields
{
    /// <summary>The fields. <paramref name="HasCaption"/> tells a caption cleared (present and empty) from none written,
    /// so clearing one doesn't bring back the Title in its place.</summary>
    public sealed record Fields(string Caption, string AltText, string ExtendedDescription, bool HasCaption = false);

    private static readonly HashSet<string> Writable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff", ".png", ".heic", ".heif", ".hif" };

    public static bool CanWrite(string path) => Writable.Contains(Path.GetExtension(path));

    private static bool IsTiff(string path) => Path.GetExtension(path).StartsWith(".tif", StringComparison.OrdinalIgnoreCase);

    public static bool IsPng(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeif(string path) => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".hif";

    public static bool IsJpeg(string path) => CanWrite(path) && !IsTiff(path) && !IsPng(path) && !IsHeif(path);

    /// <summary>Changed by editing the file's own structure, a whole <see cref="MetadataChanges"/> at once: JPEG, PNG and
    /// HEIC.</summary>
    public static bool EditsInPlace(string path) => IsJpeg(path) || IsPng(path) || IsHeif(path);

    private static string Root(string path) => IsTiff(path) ? "/ifd/xmp" : "/xmp";

    private const string Caption = "dc:description";
    private const string Alt = "Iptc4xmpCore:AltTextAccessibility";
    private const string Extended = "Iptc4xmpCore:ExtDescrAccessibility";

    /// <summary>The fields as the file has them now; empty when it has none, or isn't a format they're read from.
    /// Throws when the file can't be read.</summary>
    public static async Task<Fields> ReadAsync(string path)
    {
        if (!CanWrite(path)) return new Fields("", "", "");
        if (IsPng(path))
        {
            // Read from the XMP chunk itself; the picture isn't decoded.
            var bytes = await File.ReadAllBytesAsync(path);
            if (!PngMetadata.LooksLikePng(bytes)) throw new InvalidDataException("The file isn't the kind its name says.");
            var (caption, alt, extended) = PngMetadata.Fields(bytes);
            return new Fields(caption ?? "", alt ?? "", extended ?? "", caption is not null);
        }
        if (IsHeif(path))
        {
            // Read from the photo's XMP item: only the boxes up to the meta box, the meta box and the item are read, not
            // the pictures. Off the window's thread, as a network share can be slow.
            return await Task.Run(() =>
            {
                using var heic = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 14);
                var head = new byte[Math.Min(heic.Length, 64)];
                heic.ReadExactly(head);
                if (!HeifMetadata.LooksLikeHeif(head)) throw new InvalidDataException("The file isn't the kind its name says.");
                var (caption, alt, extended) = HeifMetadata.Fields(heic);
                return new Fields(caption ?? "", alt ?? "", extended ?? "", caption is not null);
            });
        }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var root = Root(path);
        async Task<string?> Get(string name)
        {
            // A lang-alt's default language, or a plain value written by an older tool.
            foreach (var key in new[] { $"{root}/{name}/x-default", $"{root}/{name}" })
            {
                try
                {
                    var found = await decoder.BitmapProperties.GetPropertiesAsync([key]);
                    if (found.TryGetValue(key, out var value) && value.Value is string text) return text.Trim();
                }
                catch (Exception)
                {
                    // Not there.
                }
            }
            return null;
        }
        var found = await Get(Caption);
        return new Fields(found ?? "", await Get(Alt) ?? "", await Get(Extended) ?? "", found is not null);
    }

    /// <summary>Writes the caption and accessibility fields given (null leaves one as it is, empty clears it), keeping
    /// the file's modified date, then reads them back. Throws if they didn't stick.</summary>
    public static Task WriteAsync(string path, string? caption, string? altText, string? extended) =>
        EditsInPlace(path)
            ? WriteChangesAsync(path, new MetadataChanges { Caption = caption, AltText = altText, ExtendedDescription = extended })
            : WriteTiffAsync(path, caption, altText, extended);

    /// <summary>Changes a JPEG's, PNG's or HEIC's metadata by editing its segments, chunks or boxes
    /// (<see cref="JpegMetadata"/>, <see cref="PngMetadata"/>, <see cref="HeifMetadata"/>): the caption and accessibility
    /// fields, and the Title and rating when given. Read back for the caption and accessibility fields.</summary>
    public static async Task WriteChangesAsync(string path, MetadataChanges changes)
    {
        PhotoChanges.Check();
        if (!EditsInPlace(path)) throw new NotSupportedException($"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files can't hold these fields.");
        if (changes.IsEmpty) return;
        // UTC, so the hour that repeats when daylight saving ends can't shift it.
        var modified = File.GetLastWriteTimeUtc(path);
        var before = File.ReadAllBytes(path);
        byte[] after;
        var png = IsPng(path);
        var heif = IsHeif(path);
        try
        {
            after = png ? PngMetadata.Apply(before, changes) : heif ? HeifMetadata.Apply(before, changes) : JpegMetadata.Apply(before, changes);
        }
        catch (MetadataRefusedException) when (png ? !PngMetadata.LooksLikePng(before) : heif ? !HeifMetadata.LooksLikeHeif(before) : !JpegMetadata.LooksLikeJpeg(before))
        {
            throw new InvalidDataException("The file isn't the kind its name says.");
        }
        await ReplaceAsync(path, after, () => File.GetLastWriteTimeUtc(path) == modified && new FileInfo(path).Length == before.Length);
        RestoreDate(path, modified);
        await CheckStoredAsync(path, changes.Caption, changes.AltText, changes.ExtendedDescription);
    }

    private static async Task WriteTiffAsync(string path, string? caption, string? altText, string? extended)
    {
        PhotoChanges.Check();
        if (!CanWrite(path)) throw new NotSupportedException($"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files can't hold these fields.");
        var root = Root(path);
        var set = new BitmapPropertySet();
        // Empty clears a field: WIC here can set values but not remove them, and an empty value reads as none.
        void Field(string name, string? value)
        {
            if (value is not null) set[$"{root}/<xmpalt>{name}/x-default"] = new BitmapTypedValue(value.Trim(), PropertyType.String);
        }
        Field(Caption, caption);
        Field(Alt, altText);
        Field(Extended, extended);
        if (set.Count == 0) return;

        var modified = File.GetLastWriteTimeUtc(path);
        var file = await StorageFile.GetFileFromPathAsync(path);
        InMemoryRandomAccessStream? rewritten = null;
        try
        {
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                // By what's in the file, not its name: a PNG called .tif would go to PNG's encoder.
                if (decoder.DecoderInformation.CodecId != BitmapDecoder.TiffDecoderId)
                    throw new InvalidDataException("The file isn't the kind its name says.");
                async Task Apply(BitmapEncoder encoder)
                {
                    await encoder.BitmapProperties.SetPropertiesAsync(set);
                    await encoder.FlushAsync();
                }
                try
                {
                    await Apply(await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException)
                {
                    // Read-only, in use or gone: that's the reason, not a lack of room.
                    throw;
                }
                catch (Exception)
                {
                    // No room in the file for the new fields: rewrite it, without decoding the picture.
                    rewritten = new InMemoryRandomAccessStream();
                    await Apply(await BitmapEncoder.CreateForTranscodingAsync(rewritten, decoder));
                }
            }
            if (rewritten is not null)
            {
                rewritten.Seek(0);
                using var input = rewritten.AsStreamForRead();
                using var bytes = new MemoryStream();
                await input.CopyToAsync(bytes);
                await ReplaceAsync(path, bytes.ToArray(), unchanged: null);
            }
        }
        finally
        {
            rewritten?.Dispose();
        }
        RestoreDate(path, modified);
        await CheckStoredAsync(path, caption, altText, extended);
    }

    private static void RestoreDate(string path, DateTime modified)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved, but OneDrive or a virus scanner had the file a moment: it keeps today's date.
        }
    }

    /// <summary>Read back from the file before calling it saved.</summary>
    private static async Task CheckStoredAsync(string path, string? caption, string? altText, string? extended)
    {
        if (caption is null && altText is null && extended is null) return;
        var stored = await ReadAsync(path);
        if ((caption is not null && stored.Caption != caption.Trim())
            || (altText is not null && stored.AltText != altText.Trim())
            || (extended is not null && stored.ExtendedDescription != extended.Trim()))
            throw new NotSupportedException("The value didn't stick.");
    }

    private static string TempBeside(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N")[..8]}.tmp");

    /// <summary>Saves the new file beside the photo, flushed to disk, then swaps it in whole. The photo is never emptied
    /// first, so a full disk or a crash partway leaves it as it was.</summary>
    private static async Task ReplaceAsync(string path, byte[] contents, Func<bool>? unchanged)
    {
        var temp = TempBeside(path);
        var keep = false;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                output.Write(contents);
                output.Flush(flushToDisk: true);
            }
            await SwapInAsync(temp, path, unchanged);
        }
        catch (PhotoOnlyInCopyException)
        {
            keep = true;
            throw;
        }
        finally
        {
            if (!keep) DeleteIfThere(temp);
        }
    }

    // ReplaceFile's half-done failures: the old file was moved aside, the new one not renamed into place.
    private const int UnableToMoveReplacement = unchecked((int)0x80070498);
    private const int UnableToMoveReplacement2 = unchecked((int)0x80070499);

    /// <summary>
    /// Puts <paramref name="temp"/> in place of <paramref name="path"/>, trying again while something has the photo open a
    /// moment. <paramref name="unchanged"/> is checked before every try, so a photo changed meanwhile (OneDrive bringing a
    /// newer version) isn't replaced. Windows can fail after taking the old file away but before renaming the new one
    /// (ERROR_UNABLE_TO_MOVE_REPLACEMENT): then the new one is put there by hand, and if even that fails the error says
    /// where the photo is. With the _2 variant the old file is also left under a name Windows chose.
    /// </summary>
    private static async Task SwapInAsync(string temp, string path, Func<bool>? unchanged)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (unchanged is not null && !unchanged())
                throw new IOException("It changed while it was being saved (OneDrive bringing a newer version, say), so it was left as it is. Try again.");
            try
            {
                File.Replace(temp, path, destinationBackupFileName: null);
                return;
            }
            catch (IOException ex) when (ex.HResult is UnableToMoveReplacement or UnableToMoveReplacement2 && !File.Exists(path) && File.Exists(temp))
            {
                try
                {
                    File.Move(temp, path);
                }
                catch (Exception)
                {
                    throw new PhotoOnlyInCopyException(path, temp);
                }
                return;
            }
            catch (FileNotFoundException)
            {
                // The photo was moved or deleted meanwhile: nothing to put it in place of.
                throw;
            }
            catch (IOException) when (attempt < 5)
            {
                // A decoder just used on the photo can keep it mapped until it's collected: let go of it, and try again.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100 * attempt);
            }
        }
    }

    /// <summary>Removes a temporary file. Only called when the photo is safe: in place as it was, or swapped in.</summary>
    private static void DeleteIfThere(string temp)
    {
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for clean-up; the photo itself is in place.
        }
    }
}

/// <summary>Windows stopped halfway through swapping a saved copy in: the photo is only in the copy, named here.</summary>
public sealed class PhotoOnlyInCopyException(string path, string copy) : IOException(
    $"Windows couldn't finish saving it. The photo is safe in the same folder as {Path.GetFileName(copy)}: in File Explorer, rename that to {Path.GetFileName(path)} to get it back.");

/// <summary>A photo holding a second picture (an Ultra HDR gain map, an MPO's other view) whose index can't be followed,
/// so it couldn't be kept right if the change were made (#90).</summary>
public sealed class SecondPictureException() : Exception("It holds a second picture, such as an HDR map, laid out in a way that can't be updated safely.");
