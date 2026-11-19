using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>What the scan of the video folder concluded about one video file.</summary>
public enum MusicVideoFileState
{
    /// <summary>Found and read, not yet identified.</summary>
    Pending,

    /// <summary>Identified as exactly one song and linked to it where it lies.</summary>
    Adopted,

    /// <summary>More than one song fits, or one fits too loosely; the user picks on the Match page.</summary>
    Ambiguous,

    /// <summary>No song of the library fits.</summary>
    Unmatched,

    /// <summary>The user said this file is not a video of the library; scans leave it alone.</summary>
    Ignored,

    /// <summary>The file was not found by the last scan.</summary>
    Missing,
}

/// <summary>
/// One video file found under the video root, keyed by its absolute path (ADR-0014, the 2026-11-19 amendment).
/// Stored in the <c>music_video_file</c> table. The row makes the scan incremental, and it is what the Match page
/// works through. The file itself is never renamed, moved or rewritten.
/// </summary>
public sealed class MusicVideoFile : EntityBase
{
    /// <summary>Gets or sets the absolute path of the file. Unique.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the file's last write time, in UTC.</summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>Gets or sets the container ffprobe reported, or <see langword="null"/> while the file has not been probed.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the measured duration in milliseconds, or <see langword="null"/>.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the picture width in pixels, or <see langword="null"/>.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the picture height in pixels, or <see langword="null"/>.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the artist the file name says, or <see langword="null"/>.</summary>
    public string? ParsedArtist { get; set; }

    /// <summary>Gets or sets the title the file name says, or <see langword="null"/>.</summary>
    public string? ParsedTitle { get; set; }

    /// <summary>Gets or sets the artist of the <c>.nfo</c> beside the file, or <see langword="null"/>.</summary>
    public string? NfoArtist { get; set; }

    /// <summary>Gets or sets the title of the <c>.nfo</c> beside the file, or <see langword="null"/>.</summary>
    public string? NfoTitle { get; set; }

    /// <summary>Gets or sets the artist tag of the container, or <see langword="null"/>.</summary>
    public string? TagArtist { get; set; }

    /// <summary>Gets or sets the title tag of the container, or <see langword="null"/>.</summary>
    public string? TagTitle { get; set; }

    /// <summary>Gets or sets what the scan concluded.</summary>
    public MusicVideoFileState State { get; set; }

    /// <summary>Gets or sets the song the file is linked to (<see cref="MusicVideoFileState.Adopted"/>), or <see langword="null"/>.</summary>
    public long? SongId { get; set; }

    /// <summary>Gets or sets the JSON array of up to five candidates: <c>{songId, score, reason}</c>.</summary>
    public string Candidates { get; set; } = "[]";

    /// <summary>Gets or sets the UTC instant the file was first not found, or <see langword="null"/> while it is there.</summary>
    public DateTime? MissingSince { get; set; }

    /// <summary>Gets or sets the song the file is linked to.</summary>
    public Song? Song { get; set; }
}
