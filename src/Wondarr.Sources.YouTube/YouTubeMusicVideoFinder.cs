using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Decisions;
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
/// one of its credited artists is one of the song's artists as a whole name (an artist only named in the title earns
/// ranking points, not confidence), and its title, with the video decorations stripped, is the song's at or above the decision engine's title floor.
/// YouTube Music shows a song's catalogue title and artist for an official video, not the upload's own, so such a
/// result is also checked against its real title and channel (oEmbed): the title must be the song's, the channel the
/// artist's, and it must not be audio only. A lookup that fails leaves it listed but not confident.
/// Everything else, a user upload included, is listed for a person to pick.</item>
/// </list>
/// The audio recording's own YouTube links are never candidates: they are usually the Art Track. Candidates are
/// ranked MusicBrainz first, then confident search results, then the rest, deduplicated by video id, and a
/// blocklisted video id is dropped.
/// </summary>
public sealed partial class YouTubeMusicVideoFinder : IMusicVideoFinder
{
    // The ranking is a sum of weights, so the tiers cannot be outvoted: MusicBrainz and a confident search result
    // lead by more than everything below them can add up to (1000 + 1500 + 500 + 300 + 100 = 3400).
    private const int MusicBrainzTier = 10_000;
    private const int ConfidentSearchTier = 5_000;
    private const int OfficialVideoWeight = 1_000;
    private const int ArtistWeight = 500;

    /// <summary>
    /// The most an artist-named result earns when its title is not the song's: another song of the artist is not a
    /// reason to rank it.
    /// </summary>
    private const int ArtistOnlyWeight = 50;

    /// <summary>
    /// Earned by a title that is the song's (at the title floor). It outweighs an official video's weight, so a
    /// correctly titled upload by a stranger outranks an official video of another song.
    /// </summary>
    private const int TitledWeight = 1_500;

    /// <summary>What an official video earns when its title is not the song's: it is still the better of two other songs.</summary>
    private const int UntitledOfficialWeight = 100;
    private const int TitleWeight = 300;
    private const int LengthWeight = 100;
    private const int ArtTrackPenalty = 200;

    /// <summary>How many search results are checked against their real title and channel.</summary>
    private const int MaxLookups = 6;

    /// <summary>How many of the best-ranked official videos are checked for the ranking, besides the confident ones.</summary>
    private const int RankedLookups = 5;

    /// <summary>How many lookups run at once.</summary>
    private const int LookupConcurrency = 2;

    /// <summary>Taken off a result that is not the song's artist's and says it is a live, remix, cover, vocals-only or similar version.</summary>
    private const int OtherVersionPenalty = 150;

    /// <summary>How many seconds of difference in length cost one point of the length weight.</summary>
    private const int PointsPerLengthSecond = 2;

    private readonly IMusicBrainzClient _musicBrainz;
    private readonly IInnertubeClient _innertube;
    private readonly IYouTubeOEmbedClient _oEmbed;
    private readonly ILogger<YouTubeMusicVideoFinder> _logger;

    /// <summary>Initialises a new instance of the <see cref="YouTubeMusicVideoFinder"/> class.</summary>
    /// <param name="musicBrainz">Reads the music video links of the song's recording.</param>
    /// <param name="innertube">Searches YouTube Music.</param>
    /// <param name="oEmbed">Reads an upload's real title and channel.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeMusicVideoFinder(
        IMusicBrainzClient musicBrainz,
        IInnertubeClient innertube,
        IYouTubeOEmbedClient oEmbed,
        ILogger<YouTubeMusicVideoFinder> logger)
    {
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(innertube);
        ArgumentNullException.ThrowIfNull(oEmbed);
        ArgumentNullException.ThrowIfNull(logger);

        _musicBrainz = musicBrainz;
        _innertube = innertube;
        _oEmbed = oEmbed;
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
                    Score: MusicBrainzTier + LengthPoints(query.DurationMs, target.LengthMs),
                    ArtistNamed: true,
                    TitledAsSong: true));
            }
        }

        return found;
    }

    private async Task<List<MusicVideoCandidate>> FromSearchAsync(MusicVideoQuery query, CancellationToken cancellationToken)
    {
        // The song's edit and single-version markers are left out: a video's own title seldom repeats them.
        var title = MusicVideoTitleMatcher.SearchTitle(query.Title);
        var text = $"{(query.Artists.Count > 0 ? query.Artists[0] : string.Empty)} {title}".Trim();

        var response = await _innertube
            .SearchAsync(new InnertubeSearchRequest(text, InnertubeSearchFilter.Videos), cancellationToken)
            .ConfigureAwait(false);

        var drafts = new List<Draft>();

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

            // A user upload's artist is the uploader's channel, so the artist is also looked for in the video's own
            // title ("Artist - Title", "Title - Artist", Artist "Title"); the title is then judged without it.
            var evaluation = MusicVideoTitleMatcher.Evaluate(query.Title, query.Artists, result.Title, kind != MusicVideoCandidateKind.OfficialVideo);
            var credited = result.Artists.Any(artist => ArtistEvidence.NamesAnyOf(artist, query.Artists));

            drafts.Add(new Draft(result, videoId, kind, result.Title, evaluation, credited, ArtistChannel: false, Lookup.NotAsked));
        }

        // An official video's title and artist, as YouTube Music shows them, are the song's catalogue entry and not the
        // upload's: the ones that would be confident, and the best-ranked ones, are checked against the real title.
        var checkedIds = ChooseLookups(query, drafts);

        if (checkedIds.Count > 0)
        {
            var answers = await LookUpAsync(checkedIds, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < drafts.Count; index++)
            {
                if (checkedIds.Contains(drafts[index].VideoId))
                {
                    drafts[index] = Checked(query, drafts[index], answers.GetValueOrDefault(drafts[index].VideoId));
                }
            }
        }

        var found = new List<MusicVideoCandidate>(drafts.Count);

        foreach (var draft in drafts)
        {
            // Confidence is ADR-0014's: an official video credited to the song's artist, titled as the song, from the
            // artist's own channel and not audio only. The artist in the title only earns ranking points; a user upload
            // is never confident. A result that was not checked (the lookup cap, a failed lookup) is not confident.
            var confident = draft.Kind == MusicVideoCandidateKind.OfficialVideo
                && draft.Credited
                && draft.Evaluation.IsTitledAsTheSong
                && draft.Lookup == Lookup.Passed;

            var artistNamed = draft.Evaluation.ArtistInTitle || draft.Credited || draft.ArtistChannel;
            // The loose similarity earns the artist weight; only a strict title earns the titled and official weights.
            var similar = draft.Evaluation.TitleSimilarity >= DecisionEngine.TitleFloor;
            // An official video on the artist's own channel that is the song's title plus one harmless bracket
            // ("London Bridge (Oh Snap)") is titled for ranking and Best match, but never confident.
            var titled = draft.Evaluation.IsTitledAsTheSong
                || (draft.Kind == MusicVideoCandidateKind.OfficialVideo
                    && draft.Lookup == Lookup.Passed
                    && draft.Evaluation.IsTitledWithAlternateBracket);
            var artistWeight = !artistNamed ? 0 : similar ? ArtistWeight : ArtistOnlyWeight;

            var score = (confident ? ConfidentSearchTier : 0)
                + (draft.Kind == MusicVideoCandidateKind.OfficialVideo ? (titled ? OfficialVideoWeight : UntitledOfficialWeight) : 0)
                + (titled ? TitledWeight : 0)
                - (draft.Kind is MusicVideoCandidateKind.ArtTrack or MusicVideoCandidateKind.AudioOnly ? ArtTrackPenalty : 0)
                - (!draft.Credited && draft.Evaluation.IsOtherVersion ? OtherVersionPenalty : 0)
                + artistWeight
                + (int)Math.Round(draft.Evaluation.TitleSimilarity * TitleWeight)
                + LengthPoints(query.DurationMs, draft.Result.DurationMs);

            found.Add(new MusicVideoCandidate(
                draft.VideoId,
                draft.Title,
                draft.Result.Artists,
                draft.Kind,
                draft.Result.DurationMs,
                MusicVideoCandidate.ThumbnailFor(draft.VideoId),
                confident,
                MusicVideoFoundBy.YouTubeSearch,
                score,
                artistNamed,
                titled));
        }

        return found;
    }

    /// <summary>The ids to look up: the official videos that would be confident, then the best-ranked official ones, at most <see cref="MaxLookups"/>.</summary>
    private static HashSet<string> ChooseLookups(MusicVideoQuery query, List<Draft> drafts)
    {
        var official = drafts
            .Where(draft => draft.Kind == MusicVideoCandidateKind.OfficialVideo)
            .OrderByDescending(draft => PreliminaryScore(query, draft))
            .ToList();

        var chosen = official
            .Where(draft => draft.Credited && draft.Evaluation.IsTitledAsTheSong)
            .Concat(official.Take(RankedLookups))
            .Select(draft => draft.VideoId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLookups);

        return new HashSet<string>(chosen, StringComparer.Ordinal);
    }

    /// <summary>The order the lookups are chosen in: the score before any lookup, as a confident result would have it.</summary>
    private static int PreliminaryScore(MusicVideoQuery query, Draft draft)
    {
        var titled = draft.Evaluation.IsTitledAsTheSong;
        var similar = draft.Evaluation.TitleSimilarity >= DecisionEngine.TitleFloor;
        var named = draft.Evaluation.ArtistInTitle || draft.Credited;

        return (draft.Credited && titled ? ConfidentSearchTier : 0)
            + (titled ? TitledWeight : 0)
            + (named ? (similar ? ArtistWeight : ArtistOnlyWeight) : 0)
            + LengthPoints(query.DurationMs, draft.Result.DurationMs);
    }

    /// <summary>Looks the videos up, two at a time. A video with no answer is absent from the result.</summary>
    private async Task<Dictionary<string, YouTubeOEmbed>> LookUpAsync(HashSet<string> videoIds, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(LookupConcurrency);

        var lookups = videoIds.Select(async videoId =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (VideoId: videoId, Answer: await _oEmbed.GetAsync(videoId, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Fail closed: the candidate stays listed and is not confident.
                LogLookupFailed(_logger, videoId, exception);

                return (VideoId: videoId, Answer: null);
            }
            finally
            {
                gate.Release();
            }
        });

        var answers = await Task.WhenAll(lookups).ConfigureAwait(false);

        return answers
            .Where(answer => answer.Answer is not null)
            .ToDictionary(answer => answer.VideoId, answer => answer.Answer!, StringComparer.Ordinal);
    }

    /// <summary>Judges a checked result by its real title and channel.</summary>
    private static Draft Checked(MusicVideoQuery query, Draft draft, YouTubeOEmbed? answer)
    {
        if (answer is null)
        {
            return draft with { Lookup = Lookup.Failed };
        }

        var evaluation = MusicVideoTitleMatcher.Evaluate(query.Title, query.Artists, answer.Title, draft.Kind != MusicVideoCandidateKind.OfficialVideo);
        var audioOnly = MusicVideoTitleMatcher.IsAudioOnly(query.Title, answer.Title);
        var artistChannel = MusicVideoTitleMatcher.ChannelNamesArtist(answer.AuthorName, query.Artists);

        return draft with
        {
            Title = answer.Title,
            Kind = audioOnly ? MusicVideoCandidateKind.AudioOnly : draft.Kind,
            Evaluation = evaluation,
            ArtistChannel = artistChannel,
            Lookup = !audioOnly && artistChannel ? Lookup.Passed : Lookup.Failed,
        };
    }

    private enum Lookup
    {
        NotAsked,
        Failed,
        Passed,
    }

    private sealed record Draft(
        InnertubeResult Result,
        string VideoId,
        MusicVideoCandidateKind Kind,
        string Title,
        MusicVideoTitleMatcher.Evaluation Evaluation,
        bool Credited,
        bool ArtistChannel,
        Lookup Lookup);

    /// <summary>Up to <see cref="LengthWeight"/> points for a video as long as the song, fewer the further apart they are.</summary>
    private static int LengthPoints(int? songMs, int? videoMs) =>
        songMs is { } song && videoMs is { } video
            ? Math.Max(0, LengthWeight - (Math.Abs(song - video) / 1000 * PointsPerLengthSecond))
            : 0;

    [LoggerMessage(Level = LogLevel.Debug, Message = "The real title and channel of {VideoId} could not be read; the candidate is not confident")]
    private static partial void LogLookupFailed(ILogger logger, string videoId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video lookup in {Source} failed")]
    private static partial void LogSourceFailed(ILogger logger, string source, Exception exception);
}
