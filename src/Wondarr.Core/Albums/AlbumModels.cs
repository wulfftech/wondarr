namespace Wondarr.Core.Albums;

/// <summary>Which provider an album lives on, and its id there.</summary>
/// <param name="Source">The provider: <c>musicbrainz</c> (a release-group or release MBID) or <c>deezer</c> (an album id).</param>
/// <param name="Id">The id on that provider.</param>
public sealed record AlbumRef(string Source, string Id)
{
    /// <summary>The MusicBrainz source name.</summary>
    public const string MusicBrainzSource = "musicbrainz";

    /// <summary>The Deezer source name.</summary>
    public const string DeezerSource = "deezer";

    /// <summary>True when the source is one Wondarr reads.</summary>
    public bool IsKnownSource =>
        Source is MusicBrainzSource or DeezerSource;
}

/// <summary>One hit in the album search list.</summary>
public sealed record AlbumSearchResult
{
    /// <summary>The provider the hit came from.</summary>
    public required string Source { get; init; }

    /// <summary>The release-group MBID, for a MusicBrainz hit.</summary>
    public string? ReleaseGroupId { get; init; }

    /// <summary>The Deezer album id, for a Deezer hit.</summary>
    public long? DeezerAlbumId { get; init; }

    /// <summary>The album title.</summary>
    public required string Title { get; init; }

    /// <summary>The album artist's display name.</summary>
    public required string Artist { get; init; }

    /// <summary>The year of the first release, when the provider knows it.</summary>
    public string? Year { get; init; }

    /// <summary>The type: Album, Single, EP, Compilation …</summary>
    public string? Type { get; init; }

    /// <summary>How many tracks the album holds, when the provider knows it.</summary>
    public int? TrackCount { get; init; }

    /// <summary>The cover URL, for a Deezer hit.</summary>
    public string? CoverUrl { get; init; }
}

/// <summary>One official release of a release group, as the release picker lists it.</summary>
public sealed record AlbumRelease
{
    /// <summary>The release MBID.</summary>
    public required string Id { get; init; }

    /// <summary>The release title.</summary>
    public required string Title { get; init; }

    /// <summary>The release date, when MusicBrainz knows it.</summary>
    public string? Date { get; init; }

    /// <summary>The release country, as an ISO-3166 code.</summary>
    public string? Country { get; init; }

    /// <summary>The media formats, for example <c>CD</c> or <c>CD + DVD-Video</c>.</summary>
    public string? Formats { get; init; }

    /// <summary>How many tracks the release holds.</summary>
    public int? TrackCount { get; init; }

    /// <summary>The MusicBrainz disambiguation comment.</summary>
    public string? Disambiguation { get; init; }

    /// <summary>True when this is the release an album add files the tracks under by default.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>One track of an album's tracklist, with the library's copy of it beside it.</summary>
public sealed record AlbumTrack
{
    /// <summary>The disc the track sits on, starting at 1.</summary>
    public required int Disc { get; init; }

    /// <summary>The track's position on its disc, starting at 1.</summary>
    public required int Position { get; init; }

    /// <summary>The track title.</summary>
    public required string Title { get; init; }

    /// <summary>The track's display credit.</summary>
    public required string ArtistCredit { get; init; }

    /// <summary>The track length in milliseconds, when the provider knows it.</summary>
    public int? LengthMs { get; init; }

    /// <summary>The recording MBID, for a MusicBrainz track.</summary>
    public string? MbRecordingId { get; init; }

    /// <summary>The Deezer track id, for a Deezer track.</summary>
    public long? DeezerTrackId { get; init; }

    /// <summary>The ISRCs the recording carries, upper-case.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>The song the library already holds for this track, when it does.</summary>
    public long? SongId { get; init; }

    /// <summary>The library that song is filed in.</summary>
    public long? LibraryId { get; init; }

    /// <summary>True when the library already holds this track.</summary>
    public bool Owned => SongId is not null;
}

/// <summary>What the caller wants added, and where the songs land.</summary>
public sealed record AlbumAddRequest
{
    /// <summary>The album to add.</summary>
    public required AlbumRef Album { get; init; }

    /// <summary>
    /// The track keys to add, as recording MBIDs or Deezer track ids; <see langword="null"/> adds
    /// every track the album holds.
    /// </summary>
    public IReadOnlyList<string>? TrackKeys { get; init; }

    /// <summary>The library the songs are filed in, or <see langword="null"/> for the default one.</summary>
    public long? LibraryId { get; init; }

    /// <summary>The quality profile the songs are monitored against, or <see langword="null"/> for the standard one.</summary>
    public long? QualityProfileId { get; init; }

    /// <summary>Whether the new songs are wanted.</summary>
    public bool Monitored { get; init; } = true;
}

/// <summary>One track an album add could not add, and why.</summary>
/// <param name="TrackKey">The track's key, as the caller named it.</param>
/// <param name="Title">The track title, when the tracklist knows it.</param>
/// <param name="Reason">Why the track was not added.</param>
public sealed record AlbumAddFailure(string TrackKey, string? Title, string Reason);

/// <summary>What one album add did.</summary>
public sealed record AlbumAddResult
{
    /// <summary>The album that was added.</summary>
    public required AlbumRef Album { get; init; }

    /// <summary>How many tracks became new songs.</summary>
    public required int Added { get; init; }

    /// <summary>How many tracks the library already held.</summary>
    public required int AlreadyInLibrary { get; init; }

    /// <summary>The tracks that could not be added, with the reason.</summary>
    public required IReadOnlyList<AlbumAddFailure> Failed { get; init; }

    /// <summary>
    /// How many of the added songs the library's album policy filed somewhere else, because the
    /// album's release was not among their release options.
    /// </summary>
    public required int FiledByPolicy { get; init; }

    /// <summary>The one-line summary, as the command reports it.</summary>
    public required string Message { get; init; }
}
