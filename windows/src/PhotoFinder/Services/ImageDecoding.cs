using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using BitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace PhotoFinder.Services;

/// <summary>Turns files into pictures, upright and at a sensible size. Windows' own decoder reads every
/// format the PC has a codec for (HEIC and RAW included, when their extensions are installed) and
/// applies the photo's rotation, which WPF's decoder doesn't.</summary>
public static class ImageDecoding
{
    /// <summary>Decodes an image no larger than <paramref name="maxSide"/> on its longer side.</summary>
    public static async Task<SoftwareBitmap> DecodeAsync(string path, uint maxSide)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // OrientedPixel sizes are as the photo is seen, after rotation.
        var (width, height) = FitWithin(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, maxSide);
        var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
        var transform = new BitmapTransform
        {
            // The transform scales before it rotates, so it's given the stored (unrotated) size.
            ScaledWidth = rotated ? height : width,
            ScaledHeight = rotated ? width : height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
    }

    public static (uint Width, uint Height) FitWithin(uint width, uint height, uint maxSide)
    {
        var longer = Math.Max(width, height);
        if (longer <= maxSide || longer == 0) return (Math.Max(width, 1u), Math.Max(height, 1u));
        var scale = (double)maxSide / longer;
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    /// <summary>A WPF picture of the file for the gallery and preview. Videos, and images Windows can't
    /// decode, show the shell's thumbnail instead (a video's is its poster frame).</summary>
    public static async Task<ImageSource?> LoadForDisplayAsync(string path, bool isVideo, uint maxSide)
    {
        if (!isVideo)
        {
            try
            {
                using var bitmap = await DecodeAsync(path, maxSide);
                return ToWpf(bitmap);
            }
            catch (Exception)
            {
                // Fall through to the shell's thumbnail.
            }
        }
        return await ThumbnailAsync(path, maxSide);
    }

    /// <summary>The shell's thumbnail, which works for every type with a thumbnail handler and, for cloud
    /// files, comes from the cloud provider without downloading the file.</summary>
    public static async Task<ImageSource?> ThumbnailAsync(string path, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(size <= 256 ? ThumbnailMode.ListView : ThumbnailMode.SingleItem, size,
                ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Size == 0) return null;
            using var memory = new MemoryStream();
            await thumb.AsStreamForRead().CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // The shell hands back a larger picture than asked for; thousands of list thumbnails add up.
            if (size <= 256) image.DecodePixelWidth = (int)size;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource ToWpf(SoftwareBitmap bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    /// <summary>JPEG bytes of the picture, for the Windows AI helper, which reads only common formats.</summary>
    public static async Task<byte[]> ToJpegAsync(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        // The encoder wants straight alpha; a photo has none to speak of, so ignoring it is fine.
        using var straight = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        encoder.SetSoftwareBitmap(straight);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
