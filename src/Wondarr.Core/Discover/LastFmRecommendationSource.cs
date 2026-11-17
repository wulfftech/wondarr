using Wondarr.Core.Metadata.LastFm;

namespace Wondarr.Core.Discover;

/// <summary>
/// Last.fm's <c>track.getSimilar</c> for the seed. Active only when a Last.fm key is set; the client
/// keeps its own gate, back-off and 24-hour cache.
/// </summary>
public sealed class LastFmRecommendationSource : IRecommendationSource
{
    private readonly ILastFmClient _lastFm;

    /// <summary>Initialises a new instance of the <see cref="LastFmRecommendationSource"/> class.</summary>
    /// <param name="lastFm">The Last.fm client.</param>
    public LastFmRecommendationSource(ILastFmClient lastFm)
    {
        ArgumentNullException.ThrowIfNull(lastFm);

        _lastFm = lastFm;
    }

    /// <inheritdoc />
    public string Name => "lastfm";

    /// <inheritdoc />
    public double Weight => 1.0;

    /// <inheritdoc />
    public async Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (!_lastFm.IsConfigured || string.IsNullOrWhiteSpace(seed.Artist) || string.IsNullOrWhiteSpace(seed.Title))
        {
            return RecommendationSourceResult.Skipped;
        }

        var result = await _lastFm
            .GetSimilarAsync(seed.MbRecordingId, seed.Artist, seed.Title, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            LastFmStatus.Ok => RecommendationSourceResult.Answered(
            [
                .. (result.Value ?? [])
                    .Select(track => new RecommendationCandidate(
                        track.Title,
                        track.Artist,
                        MbRecordingId: string.IsNullOrWhiteSpace(track.Mbid) ? null : track.Mbid.Trim().ToLowerInvariant())),
            ]),
            LastFmStatus.NotFound => RecommendationSourceResult.Answered([]),
            LastFmStatus.NotConfigured => RecommendationSourceResult.Skipped,
            _ => RecommendationSourceResult.Failed,
        };
    }
}
