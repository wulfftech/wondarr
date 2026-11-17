using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>Where a song's music video stands (ADR-0014).</summary>
public enum MusicVideoStatus
{
    /// <summary>A video was chosen and waits for its download.</summary>
    Queued,

    /// <summary>The video is being downloaded.</summary>
    Downloading,

    /// <summary>The video file is on disk.</summary>
    Present,

    /// <summary>The download or the check after it failed; <see cref="MusicVideo.FailureReason"/> says why.</summary>
    Failed,
}

/// <summary>How a song's music video was found (ADR-0014).</summary>
public enum MusicVideoFoundBy
{
    /// <summary>MusicBrainz links the song's recording to a video recording with a YouTube URL.</summary>
    MusicBrainz,

    /// <summary>A YouTube Music video search returned it.</summary>
    YouTubeSearch,

    /// <summary>The user picked it.</summary>
    Manual,
}

/// <summary>
/// The music video of a song: at most one per song. Stored in the <c>music_video</c> table; the
/// song's audio is never affected by it.
/// </summary>
public sealed class MusicVideo : EntityBase
{
    /// <summary>Gets or sets the song the video belongs to.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets where the video stands.</summary>
    public MusicVideoStatus Status { get; set; }

    /// <summary>Gets or sets the YouTube video id.</summary>
    public string YouTubeVideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets how the video was found.</summary>
    public MusicVideoFoundBy FoundBy { get; set; }

    /// <summary>Gets or sets the absolute path of the video file, or <see langword="null"/> until there is one.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the picture width in pixels, or <see langword="null"/> when unknown.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the picture height in pixels, or <see langword="null"/> when unknown.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the video codec, for example <c>h264</c> or <c>vp9</c>, or <see langword="null"/> when unknown.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the audio codec, for example <c>opus</c>, or <see langword="null"/> when unknown.</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Gets or sets the file size in bytes, or <see langword="null"/> when unknown.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the measured duration in milliseconds, or <see langword="null"/> when unknown.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets why the video failed, or <see langword="null"/>.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the song the video belongs to.</summary>
    public Song Song { get; set; } = null!;
}

/// <summary>
/// A YouTube video that must not be chosen again for one song: the user removed it as the wrong video.
/// Stored in the <c>music_video_blocklist</c> table, unique on the song and the video id.
/// </summary>
public sealed class MusicVideoBlocklistItem : EntityBase
{
    /// <summary>Gets or sets the song the video was rejected for.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets the rejected YouTube video id.</summary>
    public string YouTubeVideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets why the video was rejected.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the song the video was rejected for.</summary>
    public Song Song { get; set; } = null!;
}
