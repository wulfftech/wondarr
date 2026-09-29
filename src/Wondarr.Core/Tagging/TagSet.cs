namespace Wondarr.Core.Tagging;

/// <summary>
/// The complete set of tags written to an audio file (LIBRARY_OUTPUT §7.5).
/// </summary>
/// <remarks>
/// Every string member is written only when it is non-empty: the writer replaces the file's existing
/// tags rather than merging them, so an unset member means "not present on disk", never "leave the
/// source value alone".
/// </remarks>
public sealed record TagSet
{
    /// <summary>Gets the track title (ID3v2 <c>TIT2</c>, Vorbis <c>TITLE</c>, MP4 <c>©nam</c>).</summary>
    public string? Title { get; init; }

    /// <summary>Gets the display credit, e.g. <c>Daft Punk feat. Pharrell Williams</c>.</summary>
    public string? Artist { get; init; }

    /// <summary>Gets every credited artist; written as a multi-valued field.</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    /// <summary>Gets the album artist (ID3v2 <c>TPE2</c>, Vorbis <c>ALBUMARTIST</c>, MP4 <c>aART</c>).</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Gets the album title.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the track number within its disc.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Gets the number of tracks on the disc.</summary>
    public int? TrackTotal { get; init; }

    /// <summary>Gets the disc number within the release.</summary>
    public int? DiscNumber { get; init; }

    /// <summary>Gets the number of discs in the release.</summary>
    public int? DiscTotal { get; init; }

    /// <summary>
    /// Gets the album context date — identical for every file of a folder (§7.3), in
    /// <c>YYYY</c>, <c>YYYY-MM</c> or <c>YYYY-MM-DD</c> form.
    /// </summary>
    public string? Date { get; init; }

    /// <summary>Gets the recording's first release date, in the same forms as <see cref="Date"/>.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>Gets the International Standard Recording Code.</summary>
    public string? Isrc { get; init; }

    /// <summary>Gets the MusicBrainz recording id (Picard's "MusicBrainz Track Id").</summary>
    public string? MbRecordingId { get; init; }

    /// <summary>Gets the MusicBrainz release-track id, when the context is a real release.</summary>
    public string? MbReleaseTrackId { get; init; }

    /// <summary>
    /// Gets the MusicBrainz release id — one per folder, and the synthetic UUID for pseudo-albums.
    /// </summary>
    public string? MbReleaseId { get; init; }

    /// <summary>Gets the MusicBrainz release-group id.</summary>
    public string? MbReleaseGroupId { get; init; }

    /// <summary>Gets the MusicBrainz recording artist id.</summary>
    public string? MbArtistId { get; init; }

    /// <summary>Gets the MusicBrainz album artist id.</summary>
    public string? MbAlbumArtistId { get; init; }

    /// <summary>Gets the release type: <c>album</c>, <c>single</c>, <c>ep</c> or <c>compilation</c>.</summary>
    public string? ReleaseType { get; init; }

    /// <summary>Gets the release status, e.g. <c>official</c>.</summary>
    public string? ReleaseStatus { get; init; }

    /// <summary>Gets the AcoustID of the matched fingerprint.</summary>
    public string? AcoustId { get; init; }

    /// <summary>Gets a value indicating whether this is a Various Artists context.</summary>
    public bool Compilation { get; init; }

    /// <summary>Gets the front cover image (JPEG or PNG), or <see langword="null"/> for none.</summary>
    public byte[]? FrontCover { get; init; }

    /// <summary>Gets the unsynchronised lyrics text.</summary>
    public string? Lyrics { get; init; }

    /// <summary>Gets the genre; optional and off by default.</summary>
    public string? Genre { get; init; }

    /// <summary>Gets the comment; optional and off by default.</summary>
    public string? Comment { get; init; }
}
