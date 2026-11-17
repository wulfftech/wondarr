using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Metadata.ListenBrainz;

namespace Wondarr.Core.Discover;

/// <summary>
/// ListenBrainz Labs similar recordings, seeded by the song's recording MBID. Always on; a seed with no
/// MBID is skipped. A server that no longer knows the configured algorithm turns the source off for that
/// call and logs one warning per process.
/// </summary>
public sealed partial class ListenBrainzRecommendationSource : IRecommendationSource
{
    private const string CoverArtArchive = "https://coverartarchive.org/";

    private readonly IListenBrainzLabsClient _labs;
    private readonly IOptionsMonitor<DiscoverOptions> _options;
    private readonly RecommendationWarnings _warnings;
    private readonly ILogger<ListenBrainzRecommendationSource> _logger;

    /// <summary>Initialises a new instance of the <see cref="ListenBrainzRecommendationSource"/> class.</summary>
    /// <param name="labs">The ListenBrainz Labs client.</param>
    /// <param name="options">The Discover settings, which name the algorithm.</param>
    /// <param name="warnings">The process-wide record of which warnings were already logged.</param>
    /// <param name="logger">The logger.</param>
    public ListenBrainzRecommendationSource(
        IListenBrainzLabsClient labs,
        IOptionsMonitor<DiscoverOptions> options,
        RecommendationWarnings warnings,
        ILogger<ListenBrainzRecommendationSource> logger)
    {
        ArgumentNullException.ThrowIfNull(labs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(logger);

        _labs = labs;
        _options = options;
        _warnings = warnings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "listenbrainz";

    /// <inheritdoc />
    public double Weight => 1.0;

    /// <inheritdoc />
    public async Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (string.IsNullOrWhiteSpace(seed.MbRecordingId))
        {
            return RecommendationSourceResult.Skipped;
        }

        var algorithm = _options.CurrentValue.ListenBrainzAlgorithm;
        var result = await _labs
            .GetSimilarRecordingsAsync(seed.MbRecordingId, algorithm, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case ListenBrainzLabsStatus.Ok:
                return RecommendationSourceResult.Answered(
                [
                    .. result.Recordings.Select(recording => new RecommendationCandidate(
                        recording.RecordingName,
                        recording.ArtistCreditName,
                        recording.ReleaseName,
                        recording.RecordingMbid,
                        CoverUrl: recording.CaaReleaseMbid is { } release
                            ? $"{CoverArtArchive}release/{release}/front-250"
                            : null)),
                ]);

            case ListenBrainzLabsStatus.AlgorithmRejected:
                if (_warnings.FirstTime(Name + ":algorithm"))
                {
                    LogAlgorithmRejected(_logger, algorithm, result.Message ?? string.Empty);
                }

                return RecommendationSourceResult.Failed;

            default:
                return RecommendationSourceResult.Failed;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ListenBrainz Labs does not accept the algorithm '{Algorithm}' (discover.listenbrainz_algorithm), so its suggestions are skipped. It said: {Reply}")]
    private static partial void LogAlgorithmRejected(ILogger logger, string algorithm, string reply);
}
