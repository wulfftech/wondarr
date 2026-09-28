using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;

namespace Wondarr.Core.Identity;

/// <summary>One artist on a song, as resolved from MusicBrainz or Deezer.</summary>
/// <param name="Name">The credited name.</param>
/// <param name="SortName">The MusicBrainz sort name, when known.</param>
/// <param name="MbArtistId">The artist MBID, when the artist came from MusicBrainz.</param>
/// <param name="DeezerArtistId">The Deezer artist id, when the artist came from Deezer.</param>
/// <param name="Role">Whether the artist is the main artist or a featured one.</param>
/// <param name="Position">The artist's place in the credit, starting at 0.</param>
public sealed record IdentityArtist(
    string Name,
    string? SortName,
    string? MbArtistId,
    long? DeezerArtistId,
    ArtistRole Role,
    int Position);

/// <summary>
/// One song's full identity: everything the add path needs to write the song, its artists and its
/// album context. Read-only — P1-07 persists it.
/// </summary>
public sealed record SongIdentity
{
    /// <summary>The provider the identity came from: <c>musicbrainz</c> or <c>deezer</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The recording MBID, for a MusicBrainz identity.</summary>
    public string? MbRecordingId { get; init; }

    /// <summary>The Deezer track id: always set for a Deezer identity, and kept alongside an MBID when the caller knew one.</summary>
    public long? DeezerId { get; init; }

    /// <summary>The title, including any version suffix.</summary>
    public required string Title { get; init; }

    /// <summary>The display credit, for example <c>Daft Punk feat. Pharrell Williams &amp; Nile Rodgers</c>.</summary>
    public required string ArtistCredit { get; init; }

    /// <summary>The credited artists, in order.</summary>
    public IReadOnlyList<IdentityArtist> Artists { get; init; } = [];

    /// <summary>The duration in milliseconds, when the provider knows it.</summary>
    public int? DurationMs { get; init; }

    /// <summary>The ISRCs, upper-case.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>The version hints the title carries.</summary>
    public VersionFlags Flags { get; init; } = VersionFlags.None;

    /// <summary>The MusicBrainz disambiguation comment, when it carries one.</summary>
    public string? Disambiguation { get; init; }

    /// <summary>The recording's or album's first release date.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>Every release the song could be filed under (P1-05 picks one).</summary>
    public IReadOnlyList<ReleaseOption> ReleaseOptions { get; init; } = [];

    /// <summary>The cover URL, for a Deezer identity only.</summary>
    public string? CoverUrl { get; init; }
}

/// <summary>One ranked hit in the add-by-search list.</summary>
public sealed record SongCandidate
{
    /// <summary>The provider the candidate came from: <c>musicbrainz</c> or <c>deezer</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The recording MBID, for a MusicBrainz candidate.</summary>
    public string? MbRecordingId { get; init; }

    /// <summary>The Deezer track id, for a Deezer candidate.</summary>
    public long? DeezerId { get; init; }

    /// <summary>The title, including any version suffix.</summary>
    public required string Title { get; init; }

    /// <summary>The display credit.</summary>
    public required string ArtistCredit { get; init; }

    /// <summary>The duration in milliseconds, when the provider knows it.</summary>
    public int? DurationMs { get; init; }

    /// <summary>The MusicBrainz disambiguation comment, when it carries one.</summary>
    public string? Disambiguation { get; init; }

    /// <summary>The version hints the title carries.</summary>
    public VersionFlags Flags { get; init; } = VersionFlags.None;

    /// <summary>The first release date, when the provider knows it.</summary>
    public string? FirstReleaseDate { get; init; }

    /// <summary>The distinct release types of the search hit's release sample, for example <c>Album</c>.</summary>
    public IReadOnlyList<string> ReleaseTypes { get; init; } = [];

    /// <summary>The title of the search hit's release sample, when it carries one.</summary>
    public string? AlbumTitle { get; init; }

    /// <summary>The release group of the search hit's release sample, when it carries one.</summary>
    public string? ReleaseGroupId { get; init; }

    /// <summary>The cover URL: the Deezer album cover, or a Cover Art Archive link when a release group is known.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>The ISRCs, upper-case.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>The score against the query, from 0 to 100.</summary>
    public double Score { get; init; }

    /// <summary>True when the candidate was reached through the ISRC bridge from a Deezer hit.</summary>
    public bool ViaIsrc { get; init; }
}

/// <summary>How a lookup ended.</summary>
public enum ResolveStatus
{
    /// <summary>A MusicBrainz recording was found.</summary>
    Resolved,

    /// <summary>Only Deezer knows the song.</summary>
    ResolvedDeezerOnly,

    /// <summary>Neither provider knows the song.</summary>
    Unresolved,

    /// <summary>The input is something Wondarr cannot look up.</summary>
    Unsupported,
}

/// <summary>The outcome of one lookup.</summary>
public sealed record ResolveResult
{
    /// <summary>How the lookup ended.</summary>
    public required ResolveStatus Status { get; init; }

    /// <summary>The identity, for <see cref="ResolveStatus.Resolved"/> and <see cref="ResolveStatus.ResolvedDeezerOnly"/>.</summary>
    public SongIdentity? Identity { get; init; }

    /// <summary>The best candidates, for the review UI.</summary>
    public IReadOnlyList<SongCandidate> Candidates { get; init; } = [];

    /// <summary>Why the lookup ended the way it did, for <see cref="ResolveStatus.Unresolved"/> and <see cref="ResolveStatus.Unsupported"/>.</summary>
    public string? Reason { get; init; }
}
