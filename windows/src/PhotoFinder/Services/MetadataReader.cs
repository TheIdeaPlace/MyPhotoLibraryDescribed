using System.Globalization;
using PhotoFinder.Models;
using Windows.Storage;

namespace PhotoFinder.Services;

/// <summary>A labelled value for Get Info.</summary>
public sealed record InfoField(string Label, string Value);

/// <summary>Reads files' properties through the Windows property system: the same handlers File Explorer
/// uses, so JPEG, HEIC, RAW and video files all answer the same way.</summary>
public static class MetadataReader
{
    private const string DateTaken = "System.Photo.DateTaken";
    private const string DateEncoded = "System.Media.DateEncoded";
    private const string ImageWidth = "System.Image.HorizontalSize";
    private const string ImageHeight = "System.Image.VerticalSize";
    private const string FrameWidth = "System.Video.FrameWidth";
    private const string FrameHeight = "System.Video.FrameHeight";
    private const string Duration = "System.Media.Duration";
    private const string Title = "System.Title";
    private const string Keywords = "System.Keywords";
    private const string Rating = "System.Rating";
    private const string Latitude = "System.GPS.Latitude";
    private const string LatitudeRef = "System.GPS.LatitudeRef";
    private const string Longitude = "System.GPS.Longitude";
    private const string LongitudeRef = "System.GPS.LongitudeRef";
    private const string Orientation = "System.Photo.Orientation";

    private static readonly string[] ListKeys =
    [
        DateTaken, DateEncoded, ImageWidth, ImageHeight, FrameWidth, FrameHeight, Duration, Title, Keywords, Rating,
        Latitude, LatitudeRef, Longitude, LongitudeRef, Orientation,
    ];

    private static readonly string[] DetailKeys =
    [
        "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.LensModel", "System.Photo.FocalLength",
        "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.ISOSpeed", "System.Photo.Flash",
        "System.ApplicationName", "System.Comment", "System.Subject", "System.Author", "System.Video.FrameRate",
    ];

    /// <param name="forWriting">Throw when the caption and accessibility fields can't be read (the file locked a moment),
    /// rather than reading them as empty: deciding what to write from that would replace what's there.</param>
    public static async Task<MediaMetadata> ReadAsync(string path, bool forWriting = false)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.RetrievePropertiesAsync(ListKeys);
        var metadata = FromProperties(props);
        if (!XmpFields.CanWrite(path)) return metadata with { Version = MediaMetadata.CurrentVersion };
        XmpFields.Fields xmp;
        try
        {
            xmp = await XmpFields.ReadAsync(path);
        }
        catch (Exception ex) when (!forWriting && ex is MetadataRefusedException or InvalidDataException)
        {
            // Never readable (damaged, or not the kind its name says): the Title is all there is, and it isn't tried
            // again on every scan.
            return metadata with { Version = MediaMetadata.CurrentVersion };
        }
        catch (Exception) when (!forWriting)
        {
            // Not readable now: the Title is all there is, and it's read again next time (an older Version).
            return metadata;
        }
        // The standard caption field first, then the Title, where older files (and My Photo Library Described before #62) keep it. A
        // caption cleared in dc:description stays cleared, even when the Title holds something else.
        return metadata with
        {
            Caption = xmp.HasCaption ? xmp.Caption : metadata.Caption,
            AltText = xmp.AltText,
            ExtendedDescription = xmp.ExtendedDescription,
            Version = MediaMetadata.CurrentVersion,
        };
    }

    /// <summary>The file's Title (System.Title) alone, to keep it in step with the caption.</summary>
    public static async Task<string> ReadTitleAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.RetrievePropertiesAsync([Title]);
        return (Get(props, Title) as string)?.Trim() ?? "";
    }

    /// <summary>Builds metadata from raw property values. Separate from reading so it's tested without files.</summary>
    internal static MediaMetadata FromProperties(IDictionary<string, object?> props)
    {
        var width = ToInt(Get(props, ImageWidth)) is > 0 and var w ? w : ToInt(Get(props, FrameWidth));
        var height = ToInt(Get(props, ImageHeight)) is > 0 and var h ? h : ToInt(Get(props, FrameHeight));
        // Orientations 5 to 8 turn the picture on its side: the stored width is its height as seen.
        if (ToInt(Get(props, Orientation)) is >= 5 and <= 8) (width, height) = (height, width);

        return new MediaMetadata
        {
            DateTaken = ToDate(Get(props, DateTaken)) ?? ToDate(Get(props, DateEncoded)),
            Width = width,
            Height = height,
            DurationSeconds = Get(props, Duration) is { } d ? Convert.ToUInt64(d, CultureInfo.InvariantCulture) / 10_000_000.0 : 0,
            Caption = (Get(props, Title) as string)?.Trim() ?? "",
            Keywords = Get(props, Keywords) is string[] tags ? string.Join(", ", tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())) : "",
            Rating = Get(props, Rating) is { } r ? MediaTypes.StarsFromRating(Convert.ToUInt32(r, CultureInfo.InvariantCulture)) : 0,
            Latitude = Coordinate(Get(props, Latitude), Get(props, LatitudeRef) as string, "S"),
            Longitude = Coordinate(Get(props, Longitude), Get(props, LongitudeRef) as string, "W"),
        };
    }

    /// <summary>The camera, lens, exposure and other details for Get Info, in the order shown.</summary>
    public static async Task<List<InfoField>> ReadDetailsAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.RetrievePropertiesAsync(DetailKeys);
        return DetailFields(props);
    }

    internal static List<InfoField> DetailFields(IDictionary<string, object?> props)
    {
        var fields = new List<InfoField>();
        var make = (Get(props, "System.Photo.CameraManufacturer") as string)?.Trim();
        if ((Get(props, "System.Photo.CameraModel") as string)?.Trim() is { Length: > 0 } model)
        {
            var camera = !string.IsNullOrEmpty(make) && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model;
            fields.Add(new("Camera", camera));
        }
        if ((Get(props, "System.Photo.LensModel") as string)?.Trim() is { Length: > 0 } lens) fields.Add(new("Lens", lens));
        if (ToDouble(Get(props, "System.Photo.FocalLength")) is > 0 and var focal)
            fields.Add(new("Focal Length", $"{focal.ToString("0.#", CultureInfo.CurrentCulture)} mm"));
        if (ToDouble(Get(props, "System.Photo.FNumber")) is > 0 and var f)
            fields.Add(new("Aperture", $"f/{f.ToString("0.#", CultureInfo.CurrentCulture)}"));
        if (ToDouble(Get(props, "System.Photo.ExposureTime")) is > 0 and var exposure)
            fields.Add(new("Shutter Speed", ExposureText(exposure)));
        if (ToInt(Get(props, "System.Photo.ISOSpeed")) is > 0 and var iso) fields.Add(new("ISO", iso.ToString(CultureInfo.CurrentCulture)));
        if (Get(props, "System.Photo.Flash") is { } flash)
            fields.Add(new("Flash", (Convert.ToInt32(flash, CultureInfo.InvariantCulture) & 1) == 1 ? "Fired" : "Did not fire"));
        if (ToDouble(Get(props, "System.Video.FrameRate")) is > 0 and var rate)
            fields.Add(new("Frame Rate", $"{(rate / 1000).ToString("0.##", CultureInfo.CurrentCulture)} frames per second"));
        if ((Get(props, "System.Subject") as string)?.Trim() is { Length: > 0 } subject) fields.Add(new("Subject", subject));
        if ((Get(props, "System.Comment") as string)?.Trim() is { Length: > 0 } comment) fields.Add(new("Comments", comment));
        if (Get(props, "System.Author") is string[] { Length: > 0 } authors) fields.Add(new("Authors", string.Join(", ", authors)));
        if ((Get(props, "System.ApplicationName") as string)?.Trim() is { Length: > 0 } software) fields.Add(new("Software", software));
        return fields;
    }

    public static string ExposureText(double seconds) =>
        seconds < 1 ? $"1/{Math.Round(1 / seconds).ToString(CultureInfo.CurrentCulture)} second"
            : seconds == 1 ? "1 second" : $"{seconds.ToString("0.#", CultureInfo.CurrentCulture)} seconds";

    private static object? Get(IDictionary<string, object?> props, string key) =>
        props.TryGetValue(key, out var value) ? value : null;

    private static int ToInt(object? value) => value switch
    {
        null => 0,
        IConvertible c => (int)Math.Min(int.MaxValue, Convert.ToInt64(c, CultureInfo.InvariantCulture)),
        _ => 0,
    };

    private static double ToDouble(object? value) => value is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : 0;

    private static DateTime? ToDate(object? value) => value switch
    {
        DateTimeOffset o when o.Year > 1601 => o.LocalDateTime,
        DateTime d when d.Year > 1601 => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d,
        _ => null,
    };

    /// <summary>GPS coordinates are stored as degrees, minutes and seconds, with N/S or E/W separately.</summary>
    internal static double? Coordinate(object? dms, string? reference, string negative)
    {
        if (dms is not double[] { Length: >= 1 } parts) return null;
        var value = parts[0] + (parts.Length > 1 ? parts[1] / 60 : 0) + (parts.Length > 2 ? parts[2] / 3600 : 0);
        if (string.Equals(reference?.Trim(), negative, StringComparison.OrdinalIgnoreCase)) value = -value;
        return value;
    }
}
