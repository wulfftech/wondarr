namespace Wondarr.Core.Organizer;

/// <summary>
/// Everything a naming template can render (LIBRARY_OUTPUT.md §7.1). The import pipeline fills it from the
/// song, its album context and the parsed quality; the template engine never reads anything else.
/// </summary>
/// <remarks>Nullable members render as empty text; a token that renders empty makes an optional group vanish.</remarks>
public sealed record NamingValues
{
    /// <summary>The song's artist name.</summary>
    public required string ArtistName { get; init; }

    /// <summary>The album context's artist name (the same as <see cref="ArtistName"/> unless various artists).</summary>
    public string? AlbumArtistName { get; init; }

    /// <summary>The album context's title.</summary>
    public string? AlbumTitle { get; init; }

    /// <summary>The album context's shape, for example "Album" or "Single".</summary>
    public string? AlbumType { get; init; }

    /// <summary>The album context's year.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>The year of the recording's own first release.</summary>
    public int? OriginalYear { get; init; }

    /// <summary>The song's title.</summary>
    public required string TrackTitle { get; init; }

    /// <summary>The track-level artist credit, when it differs from the album artist.</summary>
    public string? TrackArtistName { get; init; }

    /// <summary>The track number inside its disc.</summary>
    public int? TrackNo { get; init; }

    /// <summary>The disc number.</summary>
    public int? DiscNo { get; init; }

    /// <summary>How many discs the release has; a value of 1 or less keeps the medium token empty.</summary>
    public int? DiscCount { get; init; }

    /// <summary>The artist's MusicBrainz id.</summary>
    public string? ArtistMbId { get; init; }

    /// <summary>The album context's MusicBrainz release id.</summary>
    public string? AlbumMbId { get; init; }

    /// <summary>The recording's MusicBrainz id.</summary>
    public string? RecordingMbId { get; init; }

    /// <summary>The MusicBrainz release id the track was taken from.</summary>
    public string? ReleaseMbId { get; init; }

    /// <summary>The recording's ISRC.</summary>
    public string? Isrc { get; init; }

    /// <summary>The quality's short title, for example "FLAC".</summary>
    public string? QualityTitle { get; init; }

    /// <summary>The quality's full title, for example "FLAC 1025 kbps".</summary>
    public string? QualityFull { get; init; }

    /// <summary>The audio codec of the file as it will be imported.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>The audio bit rate in kbps.</summary>
    public int? AudioBitRate { get; init; }

    /// <summary>The sample rate in Hz.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>The bit depth.</summary>
    public int? AudioBitsPerSample { get; init; }

    /// <summary>The source the file came from, for example "Soulseek".</summary>
    public string? Source { get; init; }

    /// <summary>The version or edition disambiguation, for example "Live".</summary>
    public string? Version { get; init; }
}

/// <summary>The knobs <see cref="NamingTemplate.Render"/> takes beyond the template and its values.</summary>
/// <param name="AsciiFold">Folds accented letters to their base letters ("Mötley Crüe" → "Motley Crue").</param>
/// <param name="Extension">The file's extension without the dot; only the last segment's 255-byte cap uses it.</param>
public sealed record NamingOptions(bool AsciiFold = false, string Extension = "mp3");
