using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// The album a song is filed and tagged under: one row per song, always, because the album assignment
/// is what the folder layout and Plex's grouping key on. Stored in the <c>album_context</c> table.
/// </summary>
public sealed class AlbumContext : EntityBase
{
    /// <summary>Gets or sets the song being filed.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets whether this is a real album, a single, an EP or a synthetic pseudo-album.</summary>
    public AlbumContextKind Kind { get; set; } = AlbumContextKind.Album;

    /// <summary>Gets or sets the album title.</summary>
    public string AlbumTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the album artist.</summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the grouping key: a real release MBID, or a synthetic pseudo-album UUID. Every
    /// song sharing a folder shares this value, so it is what "one album" means to the organizer.
    /// </summary>
    public string AlbumKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the lower-case MusicBrainz release id, or <see langword="null"/>.</summary>
    public string? MbReleaseId { get; set; }

    /// <summary>Gets or sets the lower-case MusicBrainz release group id, or <see langword="null"/>.</summary>
    public string? MbReleaseGroupId { get; set; }

    /// <summary>Gets or sets the track number within the medium, or <see langword="null"/>.</summary>
    public int? TrackNo { get; set; }

    /// <summary>Gets or sets the disc number, or <see langword="null"/>.</summary>
    public int? DiscNo { get; set; }

    /// <summary>Gets or sets the number of tracks on the release, or <see langword="null"/>.</summary>
    public int? TotalTracks { get; set; }

    /// <summary>
    /// Gets or sets the release date as <c>YYYY</c> or <c>YYYY-MM-DD</c>. Identical for every song in
    /// an album key: Plex fragments an album folder whose date tags disagree.
    /// </summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the original release date as <c>YYYY</c> or <c>YYYY-MM-DD</c>, or <see langword="null"/>.</summary>
    public string? OriginalDate { get; set; }

    /// <summary>Gets or sets the record label, or <see langword="null"/>.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the cover art URL, or <see langword="null"/>.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the album artist is "Various Artists".</summary>
    public bool IsVariousArtists { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the assignment is sticky. Adding a song never moves an
    /// existing one; only the explicit Compact task re-plans assignments.
    /// </summary>
    public bool Sticky { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the user chose this album explicitly; the Compact task
    /// never re-plans a pinned song, and the album it sits in is kept so other songs can still join it.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>Gets or sets the song being filed.</summary>
    public Song Song { get; set; } = null!;
}
