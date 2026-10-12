using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoFinder.Services;

/// <summary>
/// A photo file's XMP caption (dc:description) and IPTC accessibility fields (#31, #62), read and written through WIC,
/// since Windows' property system has no names for them. Only JPEG and TIFF, as on the Mac: PNG waits for a writer that
/// can't damage its other chunks (#61), and WIC can't write HEIC's metadata. Other formats keep their caption in
/// Explorer's Title (System.Title) instead.
///
/// - WIC's in-place encoder is used when the file has room. Otherwise its transcoder rewrites the file without
///   decoding the picture, so the pixels are the same, though a JPEG's compressed data and thumbnail may be re-packed.
///   The rewritten file is saved beside the photo and swapped in whole, so a failure partway leaves the photo as it was.
/// - Rewriting drops anything after a JPEG's end, where motion photos keep their video and Ultra HDR its gain map. Those
///   files are only changed in place, and checked afterwards (<see cref="KeepingSecondPictureAsync"/>).
/// </summary>
public static class XmpFields
{
    /// <summary>The fields. <paramref name="HasCaption"/> tells a caption cleared (present and empty) from none written,
    /// so clearing one doesn't bring back the Title in its place.</summary>
    public sealed record Fields(string Caption, string AltText, string ExtendedDescription, bool HasCaption = false);

    private static readonly HashSet<string> Writable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff" };

    public static bool CanWrite(string path) => Writable.Contains(Path.GetExtension(path));

    private static bool IsTiff(string path) => Path.GetExtension(path).StartsWith(".tif", StringComparison.OrdinalIgnoreCase);

    private static string Root(string path) => IsTiff(path) ? "/ifd/xmp" : "/xmp";

    private const string Caption = "dc:description";
    private const string Alt = "Iptc4xmpCore:AltTextAccessibility";
    private const string Extended = "Iptc4xmpCore:ExtDescrAccessibility";

    /// <summary>The fields as the file has them now; empty when it has none, or isn't a format they're read from.
    /// Throws when the file can't be read.</summary>
    public static async Task<Fields> ReadAsync(string path)
    {
        if (!CanWrite(path)) return new Fields("", "", "");
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
        var caption = await Get(Caption);
        return new Fields(caption ?? "", await Get(Alt) ?? "", await Get(Extended) ?? "", caption is not null);
    }

    /// <summary>Writes the caption and accessibility fields given (null leaves one as it is, empty clears it), keeping
    /// the file's modified date, then reads them back. Throws if they didn't stick, and
    /// <see cref="SecondPictureException"/> for a photo holding a second picture that has no room for them.</summary>
    public static async Task WriteAsync(string path, string? caption, string? altText, string? extended)
    {
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

        // UTC, so the hour that repeats when daylight saving ends can't shift it.
        var modified = File.GetLastWriteTimeUtc(path);
        await KeepingSecondPictureAsync(path, async holdsSecond =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            InMemoryRandomAccessStream? rewritten = null;
            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    // By what's in the file, not its name: a PNG called .jpg would go to PNG's encoder.
                    if (decoder.DecoderInformation.CodecId != (IsTiff(path) ? BitmapDecoder.TiffDecoderId : BitmapDecoder.JpegDecoderId))
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
                    catch (Exception) when (holdsSecond)
                    {
                        // Rewriting would lose the second picture: only a change in place will do.
                        throw new SecondPictureException();
                    }
                    catch (Exception)
                    {
                        // No room in the file for the new fields: rewrite it, without decoding the picture, with room
                        // left for next time (WIC's padding), so later changes are made in place.
                        rewritten = new InMemoryRandomAccessStream();
                        var encoder = await BitmapEncoder.CreateForTranscodingAsync(rewritten, decoder);
                        if (!IsTiff(path)) await AddRoomAsync(encoder);
                        await Apply(encoder);
                    }
                }
                if (rewritten is not null) await ReplaceAsync(path, rewritten);
            }
            finally
            {
                rewritten?.Dispose();
            }
        });
        try
        {
            File.SetLastWriteTimeUtc(path, modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved, but OneDrive or a virus scanner had the file a moment: it keeps today's date.
        }

        // Read back from the file before calling it saved.
        var stored = await ReadAsync(path);
        if ((caption is not null && stored.Caption != caption.Trim())
            || (altText is not null && stored.AltText != altText.Trim())
            || (extended is not null && stored.ExtendedDescription != extended.Trim()))
            throw new NotSupportedException("The value didn't stick.");
    }

    /// <summary>Asks WIC to leave room in a rewritten JPEG's EXIF and XMP, as Microsoft recommends for metadata that
    /// changes, so the next change fits in place.</summary>
    private static async Task AddRoomAsync(BitmapEncoder encoder)
    {
        var room = new BitmapPropertySet();
        foreach (var key in new[] { "/app1/ifd/PaddingSchema:Padding", "/app1/ifd/exif/PaddingSchema:Padding", "/xmp/PaddingSchema:Padding" })
            room[key] = new BitmapTypedValue(4096u, PropertyType.UInt32);
        try
        {
            await encoder.BitmapProperties.SetPropertiesAsync(room);
        }
        catch (Exception)
        {
            // Without room the change is still made; the next one rewrites the file again.
        }
    }

    private static string TempBeside(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N")[..8]}.tmp");

    /// <summary>Saves the rewritten file beside the photo, flushed to disk, then swaps it in whole. The photo is never
    /// emptied first, so a full disk or a crash partway leaves it as it was.</summary>
    private static async Task ReplaceAsync(string path, IRandomAccessStream rewritten)
    {
        var temp = TempBeside(path);
        try
        {
            rewritten.Seek(0);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                using var input = rewritten.AsStreamForRead();
                await input.CopyToAsync(output);
                output.Flush(flushToDisk: true);
            }
            await SwapInAsync(temp, path);
        }
        finally
        {
            DeleteIfThere(temp, path);
        }
    }

    /// <summary>Puts <paramref name="temp"/> in place of <paramref name="path"/>. Windows can fail after taking the old
    /// file away but before renaming the new one (ERROR_UNABLE_TO_MOVE_REPLACEMENT): then the new one is put there by hand,
    /// so the photo is never left missing.</summary>
    private static async Task SwapInAsync(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(temp, path, destinationBackupFileName: null);
                return;
            }
            catch (Exception) when (!File.Exists(path) && File.Exists(temp))
            {
                File.Move(temp, path);
                return;
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

    /// <summary>Removes a temporary copy, unless it's the only copy of the photo left.</summary>
    private static void DeleteIfThere(string temp, string path)
    {
        try
        {
            if (!File.Exists(temp)) return;
            if (File.Exists(path)) File.Delete(temp);
            else File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for Windows' temp clean-up; the photo itself is in place.
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/>, which is told whether the file holds a second picture. For one that does, the file
    /// is copied first, and if the write changed its size or anything after the main picture, the copy is put back and
    /// <see cref="SecondPictureException"/> thrown: WIC's in-place writer and Windows' property handler keep those, but
    /// anything that rewrites the file doesn't.
    /// </summary>
    public static async Task KeepingSecondPictureAsync(string path, Func<bool, Task> write)
    {
        if (!CanWrite(path) || IsTiff(path))
        {
            await write(false);
            return;
        }
        var before = File.ReadAllBytes(path);
        var (holds, end) = Walk(before);
        if (!holds)
        {
            await write(false);
            return;
        }
        // Not a layout this understands well enough to check afterwards: left alone.
        if (end < 0) throw new SecondPictureException();
        var backup = TempBeside(path);
        File.Copy(path, backup);
        try
        {
            Exception? failure = null;
            try
            {
                await write(true);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            if (!Kept(before, end, path))
            {
                await SwapInAsync(backup, path);
                throw new SecondPictureException();
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        finally
        {
            DeleteIfThere(backup, path);
        }
    }

    /// <summary>Whether the file is the same size and the same from the main picture's end on.</summary>
    private static bool Kept(byte[] before, int end, string path)
    {
        try
        {
            var after = File.ReadAllBytes(path);
            return after.Length == before.Length && after.AsSpan(end).SequenceEqual(before.AsSpan(end));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a JPEG holds a second picture that rewriting it would lose: data after the image's end (a motion photo's
    /// video, an Ultra HDR gain map), or a Multi-Picture Format index pointing at one. False for other formats.
    /// </summary>
    public static bool HoldsSecondPicture(string path)
    {
        if (!CanWrite(path) || IsTiff(path)) return false;
        return HoldsSecondPicture(File.ReadAllBytes(path));
    }

    internal static bool HoldsSecondPicture(byte[] b) => Walk(b).Holds;

    /// <summary>Walks a JPEG's segments. <c>End</c> is where the main picture ends (after its end marker), or -1. A layout
    /// that can't be followed counts as holding a second picture, so it's never rewritten on a guess.</summary>
    private static (bool Holds, int End) Walk(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return (false, -1);
        var multiPicture = false;
        var i = 2;
        while (i + 1 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                // Stray bytes where a marker should be: skip to the next one, as JPEG decoders do.
                i++;
                continue;
            }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }  // fill byte
            if (marker == 0xD9)
            {
                var end = i + 2;
                if (multiPicture) return (true, end);
                // Zeros after the end are padding; anything else is another picture's data.
                for (var j = end; j < b.Length; j++)
                    if (b[j] != 0) return (true, end);
                return (false, end);
            }
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }  // no length
            if (i + 3 >= b.Length) return (true, -1);
            var length = (b[i + 2] << 8) | b[i + 3];
            // A length running past the file's end: not followable.
            if (length < 2 || i + 2 + length > b.Length) return (true, -1);
            // APP2 "MPF\0": the Multi-Picture Format index of the pictures after this one.
            if (marker == 0xE2 && length >= 6 && b[i + 4] == 'M' && b[i + 5] == 'P' && b[i + 6] == 'F' && b[i + 7] == 0)
                multiPicture = true;
            i += 2 + length;
            if (marker == 0xDA)
            {
                // The compressed picture runs until the next marker: an FF that isn't stuffing (FF 00) or a restart.
                while (i + 1 < b.Length && !(b[i] == 0xFF && b[i + 1] != 0x00 && b[i + 1] is not (>= 0xD0 and <= 0xD7)))
                    i++;
            }
        }
        // No end marker (cut short): nothing after it to lose, unless it says there are more pictures.
        return (multiPicture, -1);
    }
}

/// <summary>A photo holding a second picture (a motion photo's video, an HDR gain map) that changing it would lose.</summary>
public sealed class SecondPictureException() : Exception("It holds a second picture, such as a motion photo's video or an HDR map, that changing it would lose.");
