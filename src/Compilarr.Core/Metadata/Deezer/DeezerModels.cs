namespace Compilarr.Core.Metadata.Deezer;

/// <summary>A Deezer artist, as embedded in a track, an album or a contributor list.</summary>
public sealed record DeezerArtist
{
    /// <summary>Gets the Deezer artist id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the artist's name.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>An album as referenced from a track: enough to pick a cover, little else.</summary>
public sealed record DeezerAlbumRef
{
    /// <summary>Gets the Deezer album id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the album title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the 1000×1000 cover URL, when Deezer has one.</summary>
    public string? CoverXl { get; init; }
}

/// <summary>A Deezer track: the closest thing Deezer has to "one song".</summary>
public sealed record DeezerTrack
{
    /// <summary>Gets the Deezer track id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the full title, as Deezer displays it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the shortened title, without the version suffix.</summary>
    public string? TitleShort { get; init; }

    /// <summary>Gets the version suffix, for example <c>(Remastered 2011)</c>.</summary>
    public string? TitleVersion { get; init; }

    /// <summary>Gets the ISRC, which is how a track is matched against MusicBrainz.</summary>
    public string? Isrc { get; init; }

    /// <summary>Gets the duration in seconds.</summary>
    public int Duration { get; init; }

    /// <summary>Gets the popularity rank.</summary>
    public long? Rank { get; init; }

    /// <summary>Gets a value indicating whether Deezer flags the lyrics as explicit.</summary>
    public bool ExplicitLyrics { get; init; }

    /// <summary>
    /// Gets the 30-second preview URL. It is signed and expires after roughly half an hour, so it is
    /// only ever used fresh (see <c>GetFreshPreviewUrlAsync</c>).
    /// </summary>
    public string? Preview { get; init; }

    /// <summary>Gets the performing artist.</summary>
    public DeezerArtist Artist { get; init; } = new();

    /// <summary>Gets the album the track appears on.</summary>
    public DeezerAlbumRef Album { get; init; } = new();

    /// <summary>Gets the contributing artists.</summary>
    public IReadOnlyList<DeezerArtist> Contributors { get; init; } = [];

    /// <summary>Gets the track's position on its disc, when Deezer knows it.</summary>
    public int? TrackPosition { get; init; }

    /// <summary>Gets the disc number, when Deezer knows it.</summary>
    public int? DiskNumber { get; init; }

    /// <summary>Gets the release date, as Deezer writes it (<c>yyyy-MM-dd</c>).</summary>
    public string? ReleaseDate { get; init; }
}

/// <summary>A Deezer album.</summary>
public sealed record DeezerAlbum
{
    /// <summary>Gets the Deezer album id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the album title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the record type: <c>album</c>, <c>single</c>, <c>ep</c> or <c>compile</c>.</summary>
    public string? RecordType { get; init; }

    /// <summary>Gets the release date, as Deezer writes it (<c>yyyy-MM-dd</c>).</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the barcode, which is how a release is matched against MusicBrainz.</summary>
    public string? Upc { get; init; }

    /// <summary>Gets the 1000×1000 cover URL, when Deezer has one.</summary>
    public string? CoverXl { get; init; }

    /// <summary>Gets the album artist.</summary>
    public DeezerArtist? Artist { get; init; }

    /// <summary>Gets the number of tracks on the album.</summary>
    public int? NbTracks { get; init; }
}

/// <summary>The search response envelope: <c>{ data, total }</c>.</summary>
public sealed record DeezerSearchResult
{
    /// <summary>Gets the page of tracks.</summary>
    public IReadOnlyList<DeezerTrack> Data { get; init; } = [];

    /// <summary>Gets the total number of matches, not the number returned.</summary>
    public int Total { get; init; }
}

/// <summary>
/// Deezer's error body, which arrives with <em>HTTP 200</em>: <c>{ "error": { … } }</c>.
/// </summary>
public sealed record DeezerErrorBody
{
    /// <summary>Gets the error.</summary>
    public DeezerError? Error { get; init; }
}

/// <summary>The error inside a <see cref="DeezerErrorBody"/>.</summary>
public sealed record DeezerError
{
    /// <summary>Gets the exception type, for example <c>DataException</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the human-readable message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets Deezer's error code: 4 is the quota, 800 means "no data".</summary>
    public int Code { get; init; }
}
