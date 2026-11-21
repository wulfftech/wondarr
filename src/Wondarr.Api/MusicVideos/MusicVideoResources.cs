using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

namespace Wondarr.Api.MusicVideos;

/// <summary>The music video settings, as the settings page reads them.</summary>
/// <param name="Enabled">Whether music videos are on.</param>
/// <param name="RootPath">The video root, or <see langword="null"/> when none is usable (the default would sit inside the container).</param>
/// <param name="Layout"><c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">The path of a video under the root; it may use {Artist}, {AlbumArtist}, {Title}, {Album} and {Year}.</param>
/// <param name="BesideTrackSuffix">What is added to the audio file's name under <c>beside_track</c>.</param>
/// <param name="MaxHeight">The tallest picture to download, or <see langword="null"/> for no cap.</param>
/// <param name="WriteNfo">Whether a <c>.nfo</c> file is written beside the video.</param>
/// <param name="WriteThumbnail">Whether a thumbnail is written beside the video.</param>
/// <param name="ScheduledSearchLimit">How many songs one run of the scheduled task may search for.</param>
/// <param name="ScheduledSearchEnabled">Whether the scheduled task that finds missing videos runs (off by default).</param>
/// <param name="ScheduledSearchIntervalHours">How often the scheduled task runs, in hours.</param>
/// <param name="ReadOnlyFields">camelCase names of the fields the environment sets through <c>APP__MUSIC_VIDEOS__…</c>.</param>
/// <param name="RootStatus">What a live check of the root found, or <see langword="null"/> when no root is set.</param>
/// <param name="RootNeedsConfirmation">
/// Whether the root is set but does not exist or holds no marker, so the user has to confirm it
/// (<c>POST /api/v1/musicvideo/settings/root/confirm</c>) before videos are written there.
/// </param>
/// <param name="RootPathIsDefault">Whether the root is the default, <c>&lt;media root&gt;/music-videos</c>.</param>
/// <param name="DefaultRootPath">The default root, or <see langword="null"/> when it would sit inside the container rather than on a mount, so it is not on offer.</param>
public sealed record MusicVideoSettingsResource(
    bool Enabled,
    string? RootPath,
    string Layout,
    string NamingTemplate,
    string BesideTrackSuffix,
    int? MaxHeight,
    bool WriteNfo,
    bool WriteThumbnail,
    int ScheduledSearchLimit,
    bool ScheduledSearchEnabled,
    int ScheduledSearchIntervalHours,
    IReadOnlyList<string> ReadOnlyFields,
    LibraryRootStatus? RootStatus,
    bool RootNeedsConfirmation,
    bool RootPathIsDefault,
    string? DefaultRootPath)
{
    /// <summary>Maps the service's settings.</summary>
    /// <param name="settings">The settings to map.</param>
    public static MusicVideoSettingsResource From(MusicVideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MusicVideoSettingsResource(
            settings.Enabled,
            settings.RootPath,
            settings.Layout,
            settings.NamingTemplate,
            settings.BesideTrackSuffix,
            settings.MaxHeight,
            settings.WriteNfo,
            settings.WriteThumbnail,
            settings.ScheduledSearchLimit,
            settings.ScheduledSearchEnabled,
            settings.ScheduledSearchIntervalHours,
            settings.ReadOnlyFields,
            settings.RootStatus,
            settings.RootNeedsConfirmation,
            settings.RootPathIsDefault,
            settings.DefaultRootPath);
    }
}

/// <summary>A change to the music video settings. Every field is optional; an absent field is left alone.</summary>
/// <param name="Enabled">New state of the switch.</param>
/// <param name="RootPath">New video root: an absolute path outside the libraries; an empty string goes back to the default.</param>
/// <param name="Layout">New layout, <c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">New naming template, using only {Artist}, {AlbumArtist}, {Title}, {Album} and {Year}.</param>
/// <param name="BesideTrackSuffix">New suffix for <c>beside_track</c>.</param>
/// <param name="MaxHeight">New height cap in pixels; 0 removes the cap.</param>
/// <param name="WriteNfo">New state of the <c>.nfo</c> switch.</param>
/// <param name="WriteThumbnail">New state of the thumbnail switch.</param>
/// <param name="ScheduledSearchLimit">New per-run limit of the scheduled task.</param>
/// <param name="ScheduledSearchEnabled">New state of the switch for the scheduled task.</param>
/// <param name="ScheduledSearchIntervalHours">New interval of the scheduled task, in hours.</param>
public sealed record MusicVideoSettingsUpdateResource(
    bool? Enabled = null,
    string? RootPath = null,
    string? Layout = null,
    string? NamingTemplate = null,
    string? BesideTrackSuffix = null,
    int? MaxHeight = null,
    bool? WriteNfo = null,
    bool? WriteThumbnail = null,
    int? ScheduledSearchLimit = null,
    bool? ScheduledSearchEnabled = null,
    int? ScheduledSearchIntervalHours = null)
{
    /// <summary>Maps the body to the service's change.</summary>
    public MusicVideoSettingsUpdate ToUpdate() => new(
        Enabled,
        RootPath,
        Layout,
        NamingTemplate,
        BesideTrackSuffix,
        MaxHeight,
        WriteNfo,
        WriteThumbnail,
        ScheduledSearchLimit,
        ScheduledSearchEnabled,
        ScheduledSearchIntervalHours);
}

/// <summary>A song's music video.</summary>
/// <param name="Id">The row.</param>
/// <param name="SongId">The song.</param>
/// <param name="Status">Where the video stands.</param>
/// <param name="YouTubeVideoId">The YouTube video id, or <see langword="null"/> for a video found in the folder.</param>
/// <param name="VideoTitle">The chosen YouTube video's title, or <see langword="null"/> for a video found in the folder or one chosen before the title was kept.</param>
/// <param name="FoundBy">How the video was found.</param>
/// <param name="Path">The video file's path, or <see langword="null"/> until there is one.</param>
/// <param name="Width">The picture width in pixels, or <see langword="null"/> when unknown.</param>
/// <param name="Height">The picture height in pixels, or <see langword="null"/> when unknown.</param>
/// <param name="VideoCodec">The video codec, or <see langword="null"/> when unknown.</param>
/// <param name="AudioCodec">The audio codec, or <see langword="null"/> when unknown.</param>
/// <param name="SizeBytes">The file size in bytes, or <see langword="null"/> when unknown.</param>
/// <param name="DurationMs">The measured duration in milliseconds, or <see langword="null"/> when unknown.</param>
/// <param name="FailureReason">Why the video failed, or <see langword="null"/>.</param>
/// <param name="ReplaceFailureReason">Why the last replacement of the filed video failed, or <see langword="null"/>; the video itself is unchanged.</param>
/// <param name="ReplaceFailedAt">The UTC instant the last replacement failed, or <see langword="null"/>.</param>
/// <param name="CreatedAt">The UTC instant the row was created.</param>
/// <param name="UpdatedAt">The UTC instant the row last changed.</param>
public sealed record MusicVideoResource(
    long Id,
    long SongId,
    MusicVideoStatus Status,
    string? YouTubeVideoId,
    string? VideoTitle,
    MusicVideoFoundBy FoundBy,
    string? Path,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    long? SizeBytes,
    int? DurationMs,
    string? FailureReason,
    string? ReplaceFailureReason,
    DateTime? ReplaceFailedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>Maps a row.</summary>
    /// <param name="video">The row to map.</param>
    public static MusicVideoResource From(MusicVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);

        return new MusicVideoResource(
            video.Id,
            video.SongId,
            video.Status,
            string.IsNullOrEmpty(video.YouTubeVideoId) ? null : video.YouTubeVideoId,
            video.VideoTitle,
            video.FoundBy,
            video.Path,
            video.Width,
            video.Height,
            video.VideoCodec,
            video.AudioCodec,
            video.SizeBytes,
            video.DurationMs,
            video.FailureReason,
            video.ReplaceFailureReason,
            video.ReplaceFailedAt,
            video.CreatedAt,
            video.UpdatedAt);
    }
}

/// <summary>What removing a song's music video did.</summary>
/// <param name="Path">Where the video file was, or <see langword="null"/> when the row had no file yet.</param>
/// <param name="RecycledPaths">Where the video and its sidecars are now, in the recycle bin.</param>
/// <param name="Blocklisted">Whether the YouTube video is now blocklisted for the song.</param>
public sealed record MusicVideoRemovedResource(string? Path, IReadOnlyList<string> RecycledPaths, bool Blocklisted);

/// <summary>One YouTube video that may be the song's music video.</summary>
/// <param name="VideoId">The YouTube video id.</param>
/// <param name="Title">The title the source shows.</param>
/// <param name="Artists">The artists the source credits; empty when it names none.</param>
/// <param name="Kind">What the source says the upload is.</param>
/// <param name="DurationMs">The length the source shows, in milliseconds, or <see langword="null"/>.</param>
/// <param name="ThumbnailUrl">YouTube's thumbnail for the video.</param>
/// <param name="Confident">Whether Wondarr may download it without asking.</param>
/// <param name="FoundBy">Where the candidate came from.</param>
/// <param name="Score">The ranking score: higher is better.</param>
/// <param name="ArtistNamed">Whether one of the song's artists is credited on the video or named in its title.</param>
/// <param name="TitledAsSong">Whether its title is the song's (at the title floor, studio version, not a lyric video); without it the video may be another song.</param>
public sealed record MusicVideoCandidateResource(
    string VideoId,
    string Title,
    IReadOnlyList<string> Artists,
    MusicVideoCandidateKind Kind,
    int? DurationMs,
    string ThumbnailUrl,
    bool Confident,
    MusicVideoFoundBy FoundBy,
    int Score,
    bool ArtistNamed,
    bool TitledAsSong)
{
    /// <summary>Maps a candidate.</summary>
    /// <param name="candidate">The candidate to map.</param>
    public static MusicVideoCandidateResource From(MusicVideoCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new MusicVideoCandidateResource(
            candidate.VideoId,
            candidate.Title,
            candidate.Artists,
            candidate.Kind,
            candidate.DurationMs,
            candidate.ThumbnailUrl,
            candidate.Confident,
            candidate.FoundBy,
            candidate.Score,
            candidate.ArtistNamed,
            candidate.TitledAsSong);
    }
}

/// <summary>What searching for a song's music video found.</summary>
/// <param name="Queued">Whether a confident candidate was found and its download queued.</param>
/// <param name="Candidates">The candidates, best first.</param>
/// <param name="Partial">Whether MusicBrainz did not answer, so the candidates are YouTube search results only.</param>
/// <param name="Status">Where the queued download stands (queued, or downloading once it has started); <see langword="null"/> when none was queued.</param>
public sealed record MusicVideoSearchResultResource(
    bool Queued,
    IReadOnlyList<MusicVideoCandidateResource> Candidates,
    bool Partial,
    MusicVideoStatus? Status = null);

/// <summary>A manual pick: the video to download for a song.</summary>
/// <param name="YouTubeVideoId">The YouTube video id, or a link to the video.</param>
/// <param name="VideoTitle">The video's title as the candidate list showed it, kept on the row; optional.</param>
public sealed record MusicVideoGrabResource(string? YouTubeVideoId, string? VideoTitle = null);

/// <summary>A queued download.</summary>
/// <param name="CommandId">The queued <c>MusicVideoDownload</c> command.</param>
/// <param name="Status">Where the download stands: queued while it waits for the shared yt-dlp gate, downloading once it has started.</param>
public sealed record MusicVideoGrabAcceptedResource(long CommandId, MusicVideoStatus Status);

/// <summary>Songs to search music videos for, confident matches only.</summary>
/// <param name="SongIds">The songs; at most 200.</param>
public sealed record MusicVideoBulkSearchResource(IReadOnlyList<long>? SongIds);

/// <summary>The searches a bulk request queued.</summary>
/// <param name="Queued">How many songs a search was queued for.</param>
/// <param name="CommandIds">The queued <c>MusicVideoSearch</c> commands.</param>
public sealed record MusicVideoBulkSearchAcceptedResource(int Queued, IReadOnlyList<long> CommandIds);
