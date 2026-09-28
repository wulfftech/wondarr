using Compilarr.Core.Persistence;

namespace Compilarr.Core.Domain;

/// <summary>
/// One song: the unit Compilarr searches for, grabs, imports and upgrades. Stored in the <c>song</c>
/// table. The album context and the file both point back at the song, so there is deliberately no
/// file id column here.
/// </summary>
public sealed class Song : EntityBase
{
    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the display credit, for example "Daft Punk feat. Pharrell Williams".</summary>
    public string ArtistCredit { get; set; } = string.Empty;

    /// <summary>Gets or sets the main artist's id.</summary>
    public long PrimaryArtistId { get; set; }

    /// <summary>Gets or sets the lower-case MusicBrainz recording id, or <see langword="null"/>.</summary>
    public string? MbRecordingId { get; set; }

    /// <summary>Gets or sets the lower-case MusicBrainz work id, or <see langword="null"/>.</summary>
    public string? MbWorkId { get; set; }

    /// <summary>Gets or sets the ISRCs the recording is registered under, stored as a JSON array.</summary>
    public List<string> Isrcs { get; set; } = [];

    /// <summary>Gets or sets the Spotify track id, or <see langword="null"/>.</summary>
    public string? SpotifyId { get; set; }

    /// <summary>Gets or sets the Deezer track id, or <see langword="null"/>.</summary>
    public long? DeezerId { get; set; }

    /// <summary>Gets or sets the YouTube Music video id, or <see langword="null"/>.</summary>
    public string? YtmVideoId { get; set; }

    /// <summary>Gets or sets the known duration in milliseconds, or <see langword="null"/> when unknown.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the version flags, stored as a JSON array of snake_case wire names.</summary>
    public List<string> VersionFlags { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the song is wanted.</summary>
    public bool Monitored { get; set; } = true;

    /// <summary>Gets or sets the quality profile that decides acceptable qualities and the cutoff.</summary>
    public long QualityProfileId { get; set; }

    /// <summary>Gets or sets the source profile id, or <see langword="null"/> to use the default (no FK yet).</summary>
    public long? SourceProfileId { get; set; }

    /// <summary>Gets or sets the library the song is filed in.</summary>
    public long LibraryId { get; set; }

    /// <summary>Gets or sets what added the song: <c>ui</c>, <c>api</c> or <c>list:{id}</c>.</summary>
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets free-form tags, stored as a JSON array.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Gets or sets the main artist.</summary>
    public Artist PrimaryArtist { get; set; } = null!;

    /// <summary>Gets or sets every credited artist, main first.</summary>
    public List<SongArtist> Artists { get; set; } = [];

    /// <summary>Gets or sets the album the song is filed under, or <see langword="null"/> until assigned.</summary>
    public AlbumContext? AlbumContext { get; set; }

    /// <summary>Gets or sets the imported file, or <see langword="null"/> while the song is still wanted.</summary>
    public SongFile? File { get; set; }
}

/// <summary>
/// One artist credit on a song, stored in the <c>song_artist</c> table: a song has many credits
/// (main first, then featured artists) and an artist is credited on many songs.
/// </summary>
public sealed class SongArtist : EntityBase
{
    /// <summary>Gets or sets the credited song's id.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets the credited artist's id.</summary>
    public long ArtistId { get; set; }

    /// <summary>Gets or sets whether the artist is the main artist or a featured guest.</summary>
    public ArtistRole Role { get; set; } = ArtistRole.Main;

    /// <summary>Gets or sets the credit's position, starting at 1 for the main artist.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets the song.</summary>
    public Song Song { get; set; } = null!;

    /// <summary>Gets or sets the artist.</summary>
    public Artist Artist { get; set; } = null!;
}
