using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Verification;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Finds a song's music video (ADR-0014) from two places:
/// <list type="number">
/// <item>MusicBrainz: the song's recording has a forward <c>music video</c> relation to a video recording, and that
/// recording links a YouTube watch page. Such a video is confident.</item>
/// <item>A YouTube Music video search for "artist title". A result is confident only when it is an official video,
/// one of its artists is one of the song's artists as a whole name, and its title, with the video decorations
/// stripped, is the song's at or above the decision engine's title floor. Everything else is listed for a person to
/// pick.</item>
/// </list>
/// The audio recording's own YouTube links are never candidates: they are usually the Art Track. Candidates are
/// ranked MusicBrainz first, then confident search results, then the rest, deduplicated by video id, and a
/// blocklisted video id is dropped.
/// </summary>
public sealed partial class YouTubeMusicVideoFinder : IMusicVideoFinder
{
    // The ranking is a sum of weights, so the tiers cannot be outvoted: MusicBrainz and a confident search result
    // lead by more than everything below them can add up to (1000 + 500 + 300 + 100 = 1900).
    private const int MusicBrainzTier = 10_000;
    private const int ConfidentSearchTier = 5_000;
    private const int OfficialVideoWeight = 1_000;
    private const int ArtistWeight = 500;
    private const int TitleWeight = 300;
    private const int LengthWeight = 100;
    private const int ArtTrackPenalty = 200;

    /// <summary>How many seconds of difference in length cost one point of the length weight.</summary>
    private const int PointsPerLengthSecond = 2;

    private readonly IMusicBrainzClient _musicBrainz;
    private readonly IInnertubeClient _innertube;
    private readonly ILogger<YouTubeMusicVideoFinder> _logger;

    /// <summary>Initialises a new instance of the <see cref="YouTubeMusicVideoFinder"/> class.</summary>
    /// <param name="musicBrainz">Reads the music video links of the song's recording.</param>
    /// <param name="innertube">Searches YouTube Music.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeMusicVideoFinder(
        IMusicBrainzClient musicBrainz,
        IInnertubeClient innertube,
        ILogger<YouTubeMusicVideoFinder> logger)
    {
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(innertube);
        ArgumentNullException.ThrowIfNull(logger);

        _musicBrainz = musicBrainz;
        _innertube = innertube;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = new Dictionary<string, MusicVideoCandidate>(StringComparer.Ordinal);
        Exception? failure = null;
        var partial = false;

        try
        {
            foreach (var candidate in await FromMusicBrainzAsync(query, cancellationToken).ConfigureAwait(false))
            {
                Add(candidates, candidate);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One source being down does not stop the other from answering.
            LogSourceFailed(_logger, "MusicBrainz", exception);
            failure = exception;
            partial = true;
        }

        try
        {
            foreach (var candidate in await FromSearchAsync(query, cancellationToken).ConfigureAwait(false))
            {
                Add(candidates, candidate);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSourceFailed(_logger, "YouTube Music", exception);
            failure ??= exception;
        }

        // An empty answer is only an answer when every source gave one: a failed lookup must not read as "no video".
        if (candidates.Count == 0 && failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new MusicVideoFindResult([.. candidates.Values.OrderByDescending(candidate => candidate.Score)], partial);
    }

    /// <summary>Keeps the better of two candidates for one video id: MusicBrainz outranks a search result.</summary>
    private static void Add(Dictionary<string, MusicVideoCandidate> candidates, MusicVideoCandidate candidate)
    {
        if (!candidates.TryGetValue(candidate.VideoId, out var existing) || candidate.Score > existing.Score)
        {
            candidates[candidate.VideoId] = candidate;
        }
    }

    private async Task<List<MusicVideoCandidate>> FromMusicBrainzAsync(MusicVideoQuery query, CancellationToken cancellationToken)
    {
        var found = new List<MusicVideoCandidate>();

        if (string.IsNullOrWhiteSpace(query.MbRecordingId))
        {
            return found;
        }

        var recording = await _musicBrainz.GetRecordingRelationsAsync(query.MbRecordingId, cancellationToken).ConfigureAwait(false);

        if (recording is null)
        {
            return found;
        }

        var followed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in recording.MusicVideos)
        {
            // Only the audio's own link to its video: a backward relation points the other way, and a target
            // that is not a video recording is another audio recording.
            if (!string.Equals(relation.Direction, MbMusicVideoRelation.Forward, StringComparison.OrdinalIgnoreCase)
                || !relation.TargetIsVideo
                || !followed.Add(relation.TargetRecordingId))
            {
                continue;
            }

            var target = await _musicBrainz.GetRecordingRelationsAsync(relation.TargetRecordingId, cancellationToken).ConfigureAwait(false);

            if (target is not { Video: true })
            {
                continue;
            }

            foreach (var url in target.Urls)
            {
                if (!YouTubeVideoLinks.TryParse(url.Resource, out var videoId) || query.Blocklisted.Contains(videoId))
                {
                    continue;
                }

                found.Add(new MusicVideoCandidate(
                    videoId,
                    query.Title,
                    query.Artists,
                    MusicVideoCandidateKind.Unknown,
                    target.LengthMs,
                    MusicVideoCandidate.ThumbnailFor(videoId),
                    Confident: true,
                    MusicVideoFoundBy.MusicBrainz,
                    Score: MusicBrainzTier + LengthPoints(query.DurationMs, target.LengthMs)));
            }
        }

        return found;
    }

    private async Task<List<MusicVideoCandidate>> FromSearchAsync(MusicVideoQuery query, CancellationToken cancellationToken)
    {
        var text = $"{(query.Artists.Count > 0 ? query.Artists[0] : string.Empty)} {query.Title}".Trim();
        var found = new List<MusicVideoCandidate>();

        var response = await _innertube
            .SearchAsync(new InnertubeSearchRequest(text, InnertubeSearchFilter.Videos), cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in response.Results)
        {
            if (!YouTubeVideoLinks.TryParse(YouTubeVideoLinks.WatchUrl(result.VideoId), out var videoId)
                || query.Blocklisted.Contains(videoId))
            {
                continue;
            }

            var kind = InnertubeVideoTypes.KindOf(result.MusicVideoType) switch
            {
                InnertubeVideoKind.OfficialVideo => MusicVideoCandidateKind.OfficialVideo,
                InnertubeVideoKind.ArtTrack => MusicVideoCandidateKind.ArtTrack,
                _ => MusicVideoCandidateKind.UserUpload,
            };

            var artistNamed = result.Artists.Any(artist => ArtistEvidence.NamesAnyOf(artist, query.Artists));
            var titleSimilarity = MusicVideoTitleMatcher.Similarity(query.Title, result.Title);
            var titled = MusicVideoTitleMatcher.IsTitledAsTheSong(query.Title, result.Title);
            var confident = kind == MusicVideoCandidateKind.OfficialVideo && artistNamed && titled;

            var score = (confident ? ConfidentSearchTier : 0)
                + (kind == MusicVideoCandidateKind.OfficialVideo ? OfficialVideoWeight : 0)
                - (kind == MusicVideoCandidateKind.ArtTrack ? ArtTrackPenalty : 0)
                + (artistNamed ? ArtistWeight : 0)
                + (int)Math.Round(titleSimilarity * TitleWeight)
                + LengthPoints(query.DurationMs, result.DurationMs);

            found.Add(new MusicVideoCandidate(
                videoId,
                result.Title,
                result.Artists,
                kind,
                result.DurationMs,
                MusicVideoCandidate.ThumbnailFor(videoId),
                confident,
                MusicVideoFoundBy.YouTubeSearch,
                score));
        }

        return found;
    }

    /// <summary>Up to <see cref="LengthWeight"/> points for a video as long as the song, fewer the further apart they are.</summary>
    private static int LengthPoints(int? songMs, int? videoMs) =>
        songMs is { } song && videoMs is { } video
            ? Math.Max(0, LengthWeight - (Math.Abs(song - video) / 1000 * PointsPerLengthSecond))
            : 0;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video lookup in {Source} failed")]
    private static partial void LogSourceFailed(ILogger logger, string source, Exception exception);
}
