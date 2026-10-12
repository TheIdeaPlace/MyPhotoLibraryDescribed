using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using PhotoFinder.Models;

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
///   files are changed in a working copy, in place only, checked, and then swapped in (<see cref="KeepingSecondPictureAsync"/>).
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

        // UTC, so the hour that repeats when daylight saving ends can't shift it.
        var modified = File.GetLastWriteTimeUtc(path);
        await KeepingSecondPictureAsync(path, async (target, holdsSecond) =>
        {
            var file = await StorageFile.GetFileFromPathAsync(target);
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
                    catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException)
                    {
                        // Read-only, in use or gone: that's the reason, not a lack of room.
                        throw;
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
                if (rewritten is not null) await ReplaceAsync(target, rewritten);
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

    /// <summary>A working copy beside the photo (<see cref="MediaTypes.WorkingCopyPrefix"/>), with the same extension so
    /// Windows' property handler for it applies.</summary>
    private static string WorkingCopyBeside(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{MediaTypes.WorkingCopyPrefix}{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(path)}");

    /// <summary>Saves the rewritten file beside the photo, flushed to disk, then swaps it in whole. The photo is never
    /// emptied first, so a full disk or a crash partway leaves it as it was.</summary>
    private static async Task ReplaceAsync(string path, IRandomAccessStream rewritten)
    {
        var temp = TempBeside(path);
        var keep = false;
        try
        {
            rewritten.Seek(0);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                using var input = rewritten.AsStreamForRead();
                await input.CopyToAsync(output);
                output.Flush(flushToDisk: true);
            }
            await SwapInAsync(temp, path, unchanged: null);
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

    /// <summary>
    /// Runs <paramref name="write"/> on the file it's given, told whether the photo holds a second picture. For one that
    /// does, the write is made to a working copy beside the photo, never the photo itself. The copy is checked
    /// (same size; from the main picture's compressed data on, which takes in everything after it, and its Multi-Picture
    /// index, byte for byte, in the same place; the tables before the compressed data aren't compared), flushed to disk, and only then swapped in whole. If anything rewrote or damaged it, or the photo
    /// changed meanwhile (OneDrive bringing a newer version), the copy is dropped and the photo is just as it was. WIC's
    /// in-place writer and Windows' property handler on a file with room keep the second picture; anything that rewrites
    /// the file doesn't.
    /// </summary>
    public static async Task KeepingSecondPictureAsync(string path, Func<string, bool, Task> write)
    {
        PhotoChanges.Check();
        if (!CanWrite(path) || IsTiff(path))
        {
            await write(path, false);
            return;
        }
        var stamp = File.GetLastWriteTimeUtc(path);
        var before = File.ReadAllBytes(path);
        var layout = Walk(before);
        if (!layout.Holds)
        {
            await write(path, false);
            return;
        }
        // Not a layout this understands well enough to check afterwards: left alone.
        if (layout.End < 0 || layout.Sos < 0) throw new SecondPictureException();
        var copy = WorkingCopyBeside(path);
        var keep = false;
        try
        {
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(before);
            File.SetLastWriteTimeUtc(copy, stamp);
            // Not hidden: Windows' writers refuse a hidden file. One left by a crash is a whole, visible copy.
            await write(copy, true);
            if (!Kept(before, layout, File.ReadAllBytes(copy))) throw new SecondPictureException();
            // On disk before the swap, so a power cut can't leave the photo's name on data that was never written.
            using (var flush = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                flush.Flush(flushToDisk: true);
            await SwapInAsync(copy, path, () => File.GetLastWriteTimeUtc(path) == stamp && new FileInfo(path).Length == before.Length);
        }
        catch (PhotoOnlyInCopyException)
        {
            keep = true;
            throw;
        }
        finally
        {
            if (!keep) DeleteIfThere(copy);
        }
    }

    private readonly record struct Layout(bool Holds, int End, int Mpf, int MpfLength, int Sos);

    /// <summary>Whether a changed copy kept the pictures: the same size, the Multi-Picture index (whose offsets count from
    /// it) in the same place unchanged, and everything from the main picture's compressed data on byte for byte, which
    /// takes in its end and whatever follows.</summary>
    private static bool Kept(byte[] before, Layout layout, byte[] after)
    {
        if (after.Length != before.Length) return false;
        var now = Walk(after);
        if (now.End != layout.End || now.Sos != layout.Sos || now.Mpf != layout.Mpf || now.MpfLength != layout.MpfLength) return false;
        if (layout.Mpf >= 0 && !after.AsSpan(layout.Mpf, layout.MpfLength).SequenceEqual(before.AsSpan(layout.Mpf, layout.MpfLength)))
            return false;
        return after.AsSpan(layout.Sos).SequenceEqual(before.AsSpan(layout.Sos));
    }

    /// <summary>
    /// Whether a JPEG holds a second picture that rewriting it would lose: data after the image's end (a motion photo's
    /// video, an Ultra HDR gain map), or a Multi-Picture Format index pointing at one.
    /// </summary>
    internal static bool HoldsSecondPicture(byte[] b) => Walk(b).Holds;

    /// <summary>Walks a JPEG's segments. <c>End</c> is where the main picture ends (after its end marker), or -1. A layout
    /// that can't be followed counts as holding a second picture, so it's never rewritten on a guess.</summary>
    private static Layout Walk(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return new(false, -1, -1, 0, -1);
        var multiPicture = false;
        int mpf = -1, mpfLength = 0, sos = -1;
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
                if (multiPicture) return new(true, end, mpf, mpfLength, sos);
                // Zeros after the end are padding; anything else is another picture's data.
                for (var j = end; j < b.Length; j++)
                    if (b[j] != 0) return new(true, end, mpf, mpfLength, sos);
                return new(false, end, mpf, mpfLength, sos);
            }
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }  // no length
            if (i + 3 >= b.Length) return new(true, -1, mpf, mpfLength, sos);
            var length = (b[i + 2] << 8) | b[i + 3];
            // A length running past the file's end: not followable.
            if (length < 2 || i + 2 + length > b.Length) return new(true, -1, mpf, mpfLength, sos);
            // APP2 "MPF\0": the Multi-Picture Format index of the pictures after this one.
            if (marker == 0xE2 && length >= 6 && b[i + 4] == 'M' && b[i + 5] == 'P' && b[i + 6] == 'F' && b[i + 7] == 0)
            {
                multiPicture = true;
                (mpf, mpfLength) = (i, 2 + length);
            }
            if (marker == 0xDA && sos < 0) sos = i;
            i += 2 + length;
            if (marker == 0xDA)
            {
                // The compressed picture runs until the next marker: an FF that isn't stuffing (FF 00) or a restart.
                while (i + 1 < b.Length && !(b[i] == 0xFF && b[i + 1] != 0x00 && b[i + 1] is not (>= 0xD0 and <= 0xD7)))
                    i++;
            }
        }
        // No end marker (cut short): nothing after it to lose, unless it says there are more pictures.
        return new(multiPicture, -1, mpf, mpfLength, sos);
    }
}

/// <summary>Windows stopped halfway through swapping a saved copy in: the photo is only in the copy, named here.</summary>
public sealed class PhotoOnlyInCopyException(string path, string copy) : IOException(
    $"Windows couldn't finish saving it. The photo is safe in the same folder as {Path.GetFileName(copy)}: in File Explorer, rename that to {Path.GetFileName(path)} to get it back.");

/// <summary>A photo holding a second picture (a motion photo's video, an HDR gain map) that changing it would lose.</summary>
public sealed class SecondPictureException() : Exception("It holds a second picture, such as a motion photo's video or an HDR map, that changing it would lose.");
