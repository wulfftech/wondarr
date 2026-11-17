using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

namespace Wondarr.Api.MusicVideos;

/// <summary>The music video settings, as the settings page reads them.</summary>
/// <param name="Enabled">Whether music videos are on.</param>
/// <param name="RootPath">The video root, or <see langword="null"/> until set.</param>
/// <param name="Layout"><c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">The path of a video under the root; it may use {Artist}, {AlbumArtist}, {Title}, {Album} and {Year}.</param>
/// <param name="BesideTrackSuffix">What is added to the audio file's name under <c>beside_track</c>.</param>
/// <param name="MaxHeight">The tallest picture to download, or <see langword="null"/> for no cap.</param>
/// <param name="WriteNfo">Whether a <c>.nfo</c> file is written beside the video.</param>
/// <param name="WriteThumbnail">Whether a thumbnail is written beside the video.</param>
/// <param name="ScheduledSearchLimit">How many songs one run of the scheduled task may search for.</param>
/// <param name="ReadOnlyFields">camelCase names of the fields the environment sets through <c>APP__MUSIC_VIDEOS__…</c>.</param>
/// <param name="RootStatus">What a live check of the root found, or <see langword="null"/> when no root is set.</param>
/// <param name="RootNeedsConfirmation">
/// Whether the root is set but does not exist or holds no marker, so the user has to confirm it
/// (<c>POST /api/v1/musicvideo/settings/root/confirm</c>) before videos are written there.
/// </param>
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
    IReadOnlyList<string> ReadOnlyFields,
    LibraryRootStatus? RootStatus,
    bool RootNeedsConfirmation)
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
            settings.ReadOnlyFields,
            settings.RootStatus,
            settings.RootNeedsConfirmation);
    }
}

/// <summary>A change to the music video settings. Every field is optional; an absent field is left alone.</summary>
/// <param name="Enabled">New state of the switch.</param>
/// <param name="RootPath">New video root: an absolute path outside the libraries; an empty string clears it.</param>
/// <param name="Layout">New layout, <c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">New naming template, using only {Artist}, {AlbumArtist}, {Title}, {Album} and {Year}.</param>
/// <param name="BesideTrackSuffix">New suffix for <c>beside_track</c>.</param>
/// <param name="MaxHeight">New height cap in pixels; 0 removes the cap.</param>
/// <param name="WriteNfo">New state of the <c>.nfo</c> switch.</param>
/// <param name="WriteThumbnail">New state of the thumbnail switch.</param>
/// <param name="ScheduledSearchLimit">New per-run limit of the scheduled task.</param>
public sealed record MusicVideoSettingsUpdateResource(
    bool? Enabled = null,
    string? RootPath = null,
    string? Layout = null,
    string? NamingTemplate = null,
    string? BesideTrackSuffix = null,
    int? MaxHeight = null,
    bool? WriteNfo = null,
    bool? WriteThumbnail = null,
    int? ScheduledSearchLimit = null)
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
        ScheduledSearchLimit);
}

/// <summary>A song's music video.</summary>
/// <param name="Id">The row.</param>
/// <param name="SongId">The song.</param>
/// <param name="Status">Where the video stands.</param>
/// <param name="YouTubeVideoId">The YouTube video id.</param>
/// <param name="FoundBy">How the video was found.</param>
/// <param name="Path">The video file's path, or <see langword="null"/> until there is one.</param>
/// <param name="Width">The picture width in pixels, or <see langword="null"/> when unknown.</param>
/// <param name="Height">The picture height in pixels, or <see langword="null"/> when unknown.</param>
/// <param name="VideoCodec">The video codec, or <see langword="null"/> when unknown.</param>
/// <param name="AudioCodec">The audio codec, or <see langword="null"/> when unknown.</param>
/// <param name="SizeBytes">The file size in bytes, or <see langword="null"/> when unknown.</param>
/// <param name="DurationMs">The measured duration in milliseconds, or <see langword="null"/> when unknown.</param>
/// <param name="FailureReason">Why the video failed, or <see langword="null"/>.</param>
/// <param name="CreatedAt">The UTC instant the row was created.</param>
/// <param name="UpdatedAt">The UTC instant the row last changed.</param>
public sealed record MusicVideoResource(
    long Id,
    long SongId,
    MusicVideoStatus Status,
    string YouTubeVideoId,
    MusicVideoFoundBy FoundBy,
    string? Path,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    long? SizeBytes,
    int? DurationMs,
    string? FailureReason,
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
            video.YouTubeVideoId,
            video.FoundBy,
            video.Path,
            video.Width,
            video.Height,
            video.VideoCodec,
            video.AudioCodec,
            video.SizeBytes,
            video.DurationMs,
            video.FailureReason,
            video.CreatedAt,
            video.UpdatedAt);
    }
}

/// <summary>What removing a song's music video did.</summary>
/// <param name="Path">Where the video file was, or <see langword="null"/> when the row had no file yet.</param>
/// <param name="RecycledPaths">Where the video and its sidecars are now, in the recycle bin.</param>
/// <param name="Blocklisted">Whether the YouTube video is now blocklisted for the song.</param>
public sealed record MusicVideoRemovedResource(string? Path, IReadOnlyList<string> RecycledPaths, bool Blocklisted);
