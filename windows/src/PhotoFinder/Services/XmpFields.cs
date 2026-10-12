using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoFinder.Services;

/// <summary>
/// A photo file's XMP caption (dc:description) and IPTC accessibility fields (#31, #62), read and written through WIC,
/// since Windows' property system has no names for them. Writing is lossless: WIC's in-place encoder when the file has
/// room, otherwise its transcoder, which copies a JPEG's compressed picture unchanged (checked byte for byte). Only
/// JPEG and TIFF, as on the Mac: PNG waits for a writer that can't damage its other chunks (#61), and WIC can't write
/// HEIC's metadata. Other formats keep their caption in Explorer's Title (System.Title) instead.
/// </summary>
public static class XmpFields
{
    public sealed record Fields(string Caption, string AltText, string ExtendedDescription);

    private static readonly HashSet<string> Writable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff" };

    public static bool CanWrite(string path) => Writable.Contains(Path.GetExtension(path));

    private static string Root(string path) => Path.GetExtension(path).StartsWith(".tif", StringComparison.OrdinalIgnoreCase) ? "/ifd/xmp" : "/xmp";

    private const string Caption = "dc:description";
    private const string Alt = "Iptc4xmpCore:AltTextAccessibility";
    private const string Extended = "Iptc4xmpCore:ExtDescrAccessibility";

    /// <summary>The fields as the file has them now; empty when it has none, or isn't a format they're read from.</summary>
    public static async Task<Fields> ReadAsync(string path)
    {
        if (!CanWrite(path)) return new Fields("", "", "");
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var root = Root(path);
        async Task<string> Get(string name)
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
            return "";
        }
        return new Fields(await Get(Caption), await Get(Alt), await Get(Extended));
    }

    /// <summary>Writes the caption and accessibility fields given (null leaves one as it is, empty clears it), keeping
    /// the file's modified date, then reads them back. Throws if they didn't stick.</summary>
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
        var file = await StorageFile.GetFileFromPathAsync(path);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            async Task Apply(BitmapEncoder encoder)
            {
                await encoder.BitmapProperties.SetPropertiesAsync(set);
                await encoder.FlushAsync();
            }
            try
            {
                await Apply(await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder));
            }
            catch (Exception)
            {
                // No room in the file for the new fields: rewrite it, copying the picture as it is.
                var buffer = new InMemoryRandomAccessStream();
                await Apply(await BitmapEncoder.CreateForTranscodingAsync(buffer, decoder));
                stream.Seek(0);
                stream.Size = 0;
                buffer.Seek(0);
                await RandomAccessStream.CopyAsync(buffer, stream);
                await stream.FlushAsync();
            }
        }
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
}
