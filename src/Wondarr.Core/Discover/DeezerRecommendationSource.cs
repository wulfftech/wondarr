using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;

namespace Wondarr.Core.Discover;

/// <summary>
/// Deezer's artist radio. It needs the seed's Deezer artist, which it reads from <c>track/{id}</c>, so a
/// seed with no Deezer id gets nothing from it. Deezer's radio is similarity per artist, not per track,
/// which is why the service weights it below the other two sources.
/// </summary>
public sealed class DeezerRecommendationSource : IRecommendationSource
{
    private readonly IDeezerClient _deezer;

    /// <summary>Initialises a new instance of the <see cref="DeezerRecommendationSource"/> class.</summary>
    /// <param name="deezer">The Deezer client, which goes through the quota handler.</param>
    public DeezerRecommendationSource(IDeezerClient deezer)
    {
        ArgumentNullException.ThrowIfNull(deezer);

        _deezer = deezer;
    }

    /// <inheritdoc />
    public string Name => "deezer";

    /// <inheritdoc />
    public double Weight => 0.6;

    /// <inheritdoc />
    public async Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (seed.DeezerId is not { } trackId)
        {
            return RecommendationSourceResult.Skipped;
        }

        try
        {
            var track = await _deezer.GetTrackAsync(trackId, cancellationToken).ConfigureAwait(false);
            if (track is null || track.Artist.Id <= 0)
            {
                return RecommendationSourceResult.Answered([]);
            }

            var radio = await _deezer.GetArtistRadioAsync(track.Artist.Id, cancellationToken).ConfigureAwait(false);

            return RecommendationSourceResult.Answered(
            [
                .. (radio?.Data ?? [])
                    .Where(row => row.Id > 0 && !string.IsNullOrWhiteSpace(row.Title))
                    .Select(row => new RecommendationCandidate(
                        row.Title,
                        row.Artist.Name,
                        string.IsNullOrWhiteSpace(row.Album.Title) ? null : row.Album.Title,
                        DeezerId: row.Id,
                        CoverUrl: string.IsNullOrWhiteSpace(row.Album.CoverMedium) ? null : row.Album.CoverMedium,
                        DurationMs: row.Duration > 0 ? row.Duration * 1000 : null)),
            ]);
        }
        catch (MetadataProviderException)
        {
            return RecommendationSourceResult.Failed;
        }
        catch (HttpRequestException)
        {
            return RecommendationSourceResult.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RecommendationSourceResult.Failed;
        }
    }
}
