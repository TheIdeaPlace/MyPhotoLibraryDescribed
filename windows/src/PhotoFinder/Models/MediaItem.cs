namespace PhotoFinder.Models;

/// <summary>The slow-to-read parts of a file: what the shell's property system knows about it.
/// Read in the background and cached on disk, so lists appear at once and fill in.</summary>
public sealed record MediaMetadata
{
    public DateTime? DateTaken { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double DurationSeconds { get; init; }
    /// <summary>The file's Title (System.Title), which File Explorer shows as Title and the app shows as Caption.</summary>
    public string Caption { get; init; } = "";
    public string Keywords { get; init; } = "";
    /// <summary>0 to 5 stars.</summary>
    public int Rating { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public static readonly MediaMetadata Empty = new();
}

/// <summary>One photo or video file in the library.</summary>
public sealed class MediaItem
{
    public MediaItem(string path, long size, DateTime created, DateTime modified)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        Size = size;
        Created = created;
        Modified = modified;
        Kind = MediaTypes.KindOf(path);
    }

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public long Size { get; private set; }
    public DateTime Created { get; }
    public DateTime Modified { get; private set; }
    public MediaKind Kind { get; }
    public bool IsVideo => Kind == MediaKind.Video;
    /// <summary>A cloud file not downloaded to this PC yet. Its properties aren't read in the background,
    /// because reading them would download it.</summary>
    public bool IsOnlineOnly { get; init; }

    /// <summary>Null until the properties have been read.</summary>
    public MediaMetadata? Metadata { get; set; }

    /// <summary>When the file says it was made. Copying a file gives it a new creation time but keeps
    /// its modified time, so the earlier of the two is the better guess when there's no Date Taken.</summary>
    public DateTime FileDate => Created < Modified ? Created : Modified;

    /// <summary>Date Taken when the file has one, otherwise the file's own date.</summary>
    public DateTime Date => Metadata?.DateTaken ?? FileDate;

    public bool IsFavorite => (Metadata?.Rating ?? 0) == MediaTypes.FavoriteRating;

    /// <summary>Picks up the file's new size and date after Photo Finder writes to it.</summary>
    public void RefreshFileInfo()
    {
        var info = new FileInfo(Path);
        if (!info.Exists) return;
        Size = info.Length;
        Modified = info.LastWriteTime;
    }
}
