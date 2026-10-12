using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoFinder.Tests;

/// <summary>A throwaway folder of real image files, written with real metadata through WIC, so tests
/// read and write them through the same Windows property handlers the app uses.</summary>
public sealed class TestLibrary : IDisposable
{
    public string Root { get; }

    public TestLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "PhotoFinderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public sealed record JpegOptions
    {
        public int Width { get; init; } = 64;
        public int Height { get; init; } = 48;
        public string? Title { get; init; }
        public string[]? Keywords { get; init; }
        public int Rating { get; init; }
        public DateTime? DateTaken { get; init; }
        public string? CameraMake { get; init; }
        public string? CameraModel { get; init; }
        /// <summary>EXIF orientation, 1 to 8.</summary>
        public ushort Orientation { get; init; }
        /// <summary>Text drawn on the picture, for OCR.</summary>
        public string? Text { get; init; }
    }

    public string AddJpeg(string relative, JpegOptions? options = null)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var o = options ?? new JpegOptions();
        RunSta(() => WriteJpeg(path, o));
        return path;
    }

    /// <summary>A file with an image extension that isn't a picture, or any other bytes.</summary>
    public string AddFile(string relative, string content = "not a picture")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string AddPng(string relative, int width = 40, int height = 30)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RunSta(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(SolidBitmap(width, height, Colors.SteelBlue)));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        return path;
    }

    private static void WriteJpeg(string path, JpegOptions o)
    {
        BitmapSource bitmap = o.Text is null ? SolidBitmap(o.Width, o.Height, Colors.CornflowerBlue) : TextBitmap(o.Text);
        var metadata = new BitmapMetadata("jpg");
        if (o.Title is not null) metadata.Title = o.Title;
        if (o.Keywords is not null) metadata.Keywords = new ReadOnlyCollection<string>(o.Keywords);
        if (o.Rating > 0) metadata.Rating = o.Rating;
        if (o.DateTaken is { } taken) metadata.DateTaken = taken.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        if (o.CameraMake is not null) metadata.CameraManufacturer = o.CameraMake;
        if (o.CameraModel is not null) metadata.CameraModel = o.CameraModel;
        if (o.Orientation > 0) metadata.SetQuery("/app1/ifd/{ushort=274}", o.Orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource SolidBitmap(int width, int height, Color color)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
    }

    private static BitmapSource TextBitmap(string text)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 200));
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 64, Brushes.Black, 1.0);
            dc.DrawText(formatted, new Point(30, 50));
        }
        var target = new RenderTargetBitmap(900, 200, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
    }

    internal static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Writing the test image failed.", error);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing; the temp folder is cleaned up eventually.
        }
    }
}
