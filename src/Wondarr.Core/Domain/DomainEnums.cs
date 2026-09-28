namespace Wondarr.Core.Domain;

/// <summary>What an artist contributes to a song.</summary>
public enum ArtistRole
{
    /// <summary>The song's main artist.</summary>
    Main,

    /// <summary>A guest artist named in the display credit, for example the "feat." part.</summary>
    Featured,
}

/// <summary>What kind of release a song is filed under (the album context's shape).</summary>
public enum AlbumContextKind
{
    /// <summary>A real album.</summary>
    Album,

    // "Single" is the kind's name in the data model and the wire format; CA1720 mistakes the other
    // "single" (System.Single) for it.
#pragma warning disable CA1720
    /// <summary>A single release.</summary>
    Single,
#pragma warning restore CA1720

    /// <summary>An EP.</summary>
    Ep,

    /// <summary>A compilation or "Various Artists" release.</summary>
    Compilation,

    /// <summary>A synthetic album that gathers an artist's loose singles.</summary>
    PseudoSingles,
}

/// <summary>How the album policy assigns songs to album folders.</summary>
public enum AlbumPolicy
{
    /// <summary>Greedy set cover onto the fewest real releases (the Plexamp default).</summary>
    FewestAlbums,

    /// <summary>One pseudo-album per artist.</summary>
    SinglesOnly,

    /// <summary>The earliest official album or EP a song appears on.</summary>
    OriginalAlbum,

    /// <summary>The MusicBrainz single release, one folder per song.</summary>
    SingleRelease,

    /// <summary>Everything as a Various Artists compilation.</summary>
    Compilation,
}

/// <summary>Where a library places files on disk.</summary>
public enum LibraryLayout
{
    /// <summary>One folder, no artist or album layer.</summary>
    Flat,

    /// <summary>A folder per artist.</summary>
    Artist,

    /// <summary>A folder per artist, then per album.</summary>
    ArtistAlbum,

    /// <summary>The Plexamp preset: album layer plus the compaction policy and Plex-safe tags.</summary>
    Plexamp,
}
