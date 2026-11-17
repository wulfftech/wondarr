namespace Wondarr.Core.Discover;

/// <summary>A library song a source is asked to find neighbours of.</summary>
/// <param name="SongId">The Wondarr song id.</param>
/// <param name="Title">The song's title.</param>
/// <param name="Artist">The main artist's name.</param>
/// <param name="MbRecordingId">The lower-case recording MBID, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id, or <see langword="null"/>.</param>
public sealed record RecommendationSeed(
    long SongId,
    string Title,
    string Artist,
    string? MbRecordingId,
    long? DeezerId);

/// <summary>One song a source suggests. A source lists them best first; the order is the source's whole opinion.</summary>
/// <param name="Title">The title.</param>
/// <param name="Artist">The artist credit.</param>
/// <param name="Album">The album or release name, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The lower-case recording MBID, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id, or <see langword="null"/>.</param>
/// <param name="CoverUrl">A cover image URL, or <see langword="null"/>.</param>
/// <param name="DurationMs">The duration in milliseconds, or <see langword="null"/>.</param>
public sealed record RecommendationCandidate(
    string Title,
    string Artist,
    string? Album = null,
    string? MbRecordingId = null,
    string? Isrc = null,
    long? DeezerId = null,
    string? CoverUrl = null,
    int? DurationMs = null);

/// <summary>How one source's call for one seed ended.</summary>
public enum RecommendationSourceStatus
{
    /// <summary>The source answered; its list may be empty.</summary>
    Answered,

    /// <summary>The source had nothing to ask: no key, or the seed lacks the id it needs.</summary>
    Skipped,

    /// <summary>The source was asked and failed: a timeout, a throttle, a 5xx, a rejected algorithm.</summary>
    Failed,
}

/// <summary>What a source returned for one seed.</summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Candidates">The suggestions, best first; empty unless <see cref="RecommendationSourceStatus.Answered"/>.</param>
public sealed record RecommendationSourceResult(
    RecommendationSourceStatus Status,
    IReadOnlyList<RecommendationCandidate> Candidates)
{
    /// <summary>A source that had nothing to ask.</summary>
    public static RecommendationSourceResult Skipped { get; } = new(RecommendationSourceStatus.Skipped, []);

    /// <summary>A source that was asked and failed.</summary>
    public static RecommendationSourceResult Failed { get; } = new(RecommendationSourceStatus.Failed, []);

    /// <summary>A source that answered with <paramref name="candidates"/>.</summary>
    /// <param name="candidates">The suggestions, best first.</param>
    public static RecommendationSourceResult Answered(IReadOnlyList<RecommendationCandidate> candidates) =>
        new(RecommendationSourceStatus.Answered, candidates);
}

/// <summary>A place Discover asks for suggestions. It makes no MusicBrainz request, ever.</summary>
public interface IRecommendationSource
{
    /// <summary>Gets the source's stable name: <c>listenbrainz</c>, <c>lastfm</c> or <c>deezer</c>.</summary>
    string Name { get; }

    /// <summary>Gets how much the source's rank counts towards a suggestion's score.</summary>
    double Weight { get; }

    /// <summary>Asks for songs similar to <paramref name="seed"/>. A failure is a status, never an exception.</summary>
    /// <param name="seed">The library song.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken);
}
