using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Identity;

/// <summary>
/// Turns whatever a user types or pastes into a song's identity: the MusicBrainz recording when one
/// exists, otherwise the Deezer track, otherwise "unresolved". Read-only — nothing is written.
/// </summary>
public interface IIdentityResolver
{
    /// <summary>
    /// Finds the one identity behind an input, for bulk adds.
    /// </summary>
    /// <param name="raw">What the user typed or pasted.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The identity, or the reason there is none.</returns>
    Task<ResolveResult> ResolveAsync(string raw, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ranks the candidates behind a text query, for the add-by-search UI. An id input has nothing to
    /// rank: <see cref="ResolveAsync"/> answers it directly.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <param name="limit">How many candidates to return at most.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The candidates, best first.</returns>
    Task<IReadOnlyList<SongCandidate>> SearchAsync(string raw, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="SearchAsync"/> that survives one provider being down: the other provider's candidates
    /// come back with the key of the one that did not answer.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <param name="limit">How many candidates to return at most.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The candidates, best first, and the providers that failed.</returns>
    /// <exception cref="ProvidersUnavailableException">Every provider the search needed failed.</exception>
    Task<PartialSearch<SongCandidate>> SearchPartialAsync(string raw, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the full identity of a song from MusicBrainz (when an MBID is given) or Deezer. At least
    /// one id must be given; a Deezer id passed with an MBID is kept on the identity.
    /// </summary>
    /// <param name="mbRecordingId">The recording MBID, when known.</param>
    /// <param name="deezerTrackId">The Deezer track id, when known.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The identity, or <see langword="null"/> when the provider does not know the id.</returns>
    Task<SongIdentity?> GetIdentityAsync(
        string? mbRecordingId,
        long? deezerTrackId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The identity resolver (MATCHING_ENGINE.md §6.3). Deezer is searched first because its search is
/// forgiving and its hits carry an ISRC, which bridges straight to the MusicBrainz recording.
/// </summary>
public sealed partial class IdentityResolver : IIdentityResolver
{
    /// <summary>The source name of a MusicBrainz identity.</summary>
    public const string MusicBrainzSource = "musicbrainz";

    /// <summary>The source name of a Deezer identity.</summary>
    public const string DeezerSource = "deezer";

    /// <summary>MusicBrainz' "Various Artists" artist MBID.</summary>
    public const string VariousArtistsId = "89ad4ac3-39f7-470e-963a-56509c546377";

    /// <summary>How many candidates a resolve keeps for the review UI.</summary>
    private const int ReviewCandidateLimit = 5;

    /// <summary>How many MusicBrainz search results a lookup asks for.</summary>
    private const int SearchLimit = 25;

    /// <summary>How many hits the title-only Deezer retry asks for.</summary>
    private const int RetrySearchLimit = 50;

    /// <summary>
    /// How far a candidate's length may differ from a known reference length and still be acceptable.
    /// It is deliberately looser than <see cref="DurationCutoffMs"/>: a live or remastered pressing
    /// differs by seconds without being another song.
    /// </summary>
    private const int DurationAcceptanceMs = 10000;

    /// <summary>How many candidate identities a resolve reads while looking for an official release.</summary>
    private const int IdentityAttempts = 3;

    /// <summary>The lowest total score a candidate may have and still be accepted.</summary>
    private const double AcceptScore = 70;

    /// <summary>The lowest title similarity an accepted candidate may have.</summary>
    private const double AcceptTitle = 0.85;

    /// <summary>The lowest artist similarity an accepted candidate may have.</summary>
    private const double AcceptArtist = 0.80;

    /// <summary>The durations two recordings may differ by and still score full marks.</summary>
    private static readonly int DurationToleranceMs = 2000;

    /// <summary>The duration difference at which the duration score reaches zero.</summary>
    private static readonly int DurationCutoffMs = 15000;

    /// <summary>Score weights, in one place so the shape of the score is readable.</summary>
    private const double TitleWeight = 40;
    private const double ArtistWeight = 30;
    private const double FlagsScore = 15;
    private const double DurationScoreWeight = 15;
    private const double DurationFallback = 7.5;
    private const double IsrcBridgeBonus = 10;
    private const double VideoPenalty = 20;

    private readonly IMusicBrainzClient _musicBrainz;
    private readonly IDeezerClient _deezer;
    private readonly IOptions<MetadataOptions> _options;
    private readonly ILogger<IdentityResolver> _logger;

    /// <summary>Initialises a new instance of the <see cref="IdentityResolver"/> class.</summary>
    /// <param name="musicBrainz">The MusicBrainz client.</param>
    /// <param name="deezer">The Deezer client.</param>
    /// <param name="options">The metadata options, for the Cover Art Archive base URL.</param>
    /// <param name="logger">The logger.</param>
    public IdentityResolver(
        IMusicBrainzClient musicBrainz,
        IDeezerClient deezer,
        IOptions<MetadataOptions> options,
        ILogger<IdentityResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(deezer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _musicBrainz = musicBrainz;
        _deezer = deezer;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ResolveResult> ResolveAsync(string raw, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var input = LookupInput.Parse(raw);

        LogResolving(_logger, input.Kind, input.Raw);

        return input.Kind switch
        {
            LookupKind.Unsupported => new ResolveResult
            {
                Status = ResolveStatus.Unsupported,
                Reason = input.UnsupportedReason,
            },
            LookupKind.MbRecordingId => await ResolveRecordingAsync(input, cancellationToken).ConfigureAwait(false),
            LookupKind.Isrc => await ResolveIsrcAsync(input, cancellationToken).ConfigureAwait(false),
            LookupKind.DeezerTrackId => await ResolveDeezerTrackAsync(input, cancellationToken).ConfigureAwait(false),
            _ => await ResolveTextAsync(input, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SongCandidate>> SearchAsync(
        string raw,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "At least one candidate must be asked for.");
        }

        var input = LookupInput.Parse(raw);

        if (input.Kind is not (LookupKind.ArtistTitle or LookupKind.FreeText))
        {
            // An id is not a search: the caller resolves it directly.
            return [];
        }

        var outcome = await RunTextPipelineAsync(input, null, cancellationToken).ConfigureAwait(false);

        return Rank(BuildCandidates(outcome, ReviewCandidateLimit * 10), limit);
    }

    /// <inheritdoc />
    public async Task<PartialSearch<SongCandidate>> SearchPartialAsync(
        string raw,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "At least one candidate must be asked for.");
        }

        var input = LookupInput.Parse(raw);

        if (input.Kind is not (LookupKind.ArtistTitle or LookupKind.FreeText))
        {
            return new PartialSearch<SongCandidate>([], []);
        }

        var tracker = new ProviderTracker();
        var outcome = await RunTextPipelineAsync(input, tracker, cancellationToken).ConfigureAwait(false);

        // Nobody answered: there is nothing to show, and "no results" would be a lie.
        if (tracker.Failed.Count > 0 && tracker.Succeeded.Count == 0)
        {
            throw new ProvidersUnavailableException(tracker.Failed, tracker.FirstFailure);
        }

        return new PartialSearch<SongCandidate>(
            Rank(BuildCandidates(outcome, ReviewCandidateLimit * 10), limit),
            tracker.Failed);
    }

    /// <inheritdoc />
    public async Task<SongIdentity?> GetIdentityAsync(
        string? mbRecordingId,
        long? deezerTrackId,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(mbRecordingId))
        {
            var recording = await _musicBrainz.GetRecordingAsync(mbRecordingId, cancellationToken).ConfigureAwait(false);
            if (recording is null)
            {
                return null;
            }

            // A recording lookup caps inc=releases at 25 releases, so the full list comes from the browse.
            var releases = await _musicBrainz
                .GetReleasesForRecordingAsync(recording.Id, 3, cancellationToken)
                .ConfigureAwait(false);

            return BuildMusicBrainzIdentity(recording, releases, deezerTrackId);
        }

        if (deezerTrackId is not null)
        {
            var track = await _deezer.GetTrackAsync(deezerTrackId.Value, cancellationToken).ConfigureAwait(false);

            return track is null ? null : await BuildDeezerIdentityAsync(track, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Resolves a recording id straight off the MusicBrainz.</summary>
    private async Task<ResolveResult> ResolveRecordingAsync(LookupInput input, CancellationToken cancellationToken)
    {
        var identity = await GetIdentityAsync(input.MbRecordingId, null, cancellationToken).ConfigureAwait(false);

        if (identity is null)
        {
            return Unresolved(input, $"MusicBrainz does not know the recording {input.MbRecordingId}");
        }

        LogResolvedRecording(_logger, input.Raw, identity.MbRecordingId!);

        return new ResolveResult { Status = ResolveStatus.Resolved, Identity = identity };
    }

    /// <summary>Resolves a bare ISRC: MusicBrainz first, then Deezer.</summary>
    private async Task<ResolveResult> ResolveIsrcAsync(LookupInput input, CancellationToken cancellationToken)
    {
        var recordings = await _musicBrainz
            .GetRecordingsByIsrcAsync(input.Isrc!, cancellationToken)
            .ConfigureAwait(false);

        var chosen = ChooseByIsrc(recordings);
        if (chosen is not null)
        {
            // A bare ISRC has no query to score against, so the recordings keep their own order: the one
            // ChooseByIsrc picked first, then the rest.
            var ranked = new List<MbRecording>(recordings.Count) { chosen };
            foreach (var recording in recordings)
            {
                if (!string.Equals(recording.Id, chosen.Id, StringComparison.Ordinal))
                {
                    ranked.Add(recording);
                }
            }

            var pick = await IdentityWithReleasesAsync(ranked, null, cancellationToken).ConfigureAwait(false);
            if (pick is not null)
            {
                LogIsrcToRecording(_logger, input.Isrc!, pick.Identity.MbRecordingId!);

                return new ResolveResult { Status = ResolveStatus.Resolved, Identity = pick.Identity };
            }
        }

        var track = await _deezer.GetTrackByIsrcAsync(input.Isrc!, cancellationToken).ConfigureAwait(false);
        if (track is not null)
        {
            LogIsrcToDeezer(_logger, input.Isrc!, track.Id);

            return await DeezerOnlyAsync(track, cancellationToken).ConfigureAwait(false);
        }

        return Unresolved(input, $"No match on MusicBrainz or Deezer for '{input.Raw}'");
    }

    /// <summary>Resolves a Deezer track id, bridging to MusicBrainz through its ISRC.</summary>
    private async Task<ResolveResult> ResolveDeezerTrackAsync(LookupInput input, CancellationToken cancellationToken)
    {
        var track = await _deezer.GetTrackAsync(input.DeezerTrackId!.Value, cancellationToken).ConfigureAwait(false);

        if (track is null)
        {
            return Unresolved(input, $"Deezer does not know the track {input.DeezerTrackId}");
        }

        var query = QueryFromDeezerTrack(track);
        var bridged = await BridgeToMusicBrainzAsync(query, track, track.Duration * 1000, cancellationToken)
            .ConfigureAwait(false);

        if (bridged.Count > 0)
        {
            var pick = await IdentityWithReleasesAsync(bridged, track.Id, cancellationToken).ConfigureAwait(false);
            if (pick is not null)
            {
                LogDeezerToRecording(_logger, track.Id, pick.Identity.MbRecordingId!);

                return new ResolveResult { Status = ResolveStatus.Resolved, Identity = pick.Identity };
            }
        }

        return await DeezerOnlyAsync(track, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the artist-title and free-text pipeline: Deezer, the ISRC bridge, then MusicBrainz search.</summary>
    private async Task<ResolveResult> ResolveTextAsync(LookupInput input, CancellationToken cancellationToken)
    {
        var outcome = await RunTextPipelineAsync(input, null, cancellationToken).ConfigureAwait(false);

        if (outcome.Ranked.Count > 0)
        {
            var pick = await IdentityWithReleasesAsync(RecordingsOf(outcome.Ranked), null, cancellationToken)
                .ConfigureAwait(false);

            if (pick is not null)
            {
                LogScored(_logger, input.Raw, pick.Identity.MbRecordingId!, outcome.Ranked[pick.Rank].Score.Total);

                return new ResolveResult { Status = ResolveStatus.Resolved, Identity = pick.Identity };
            }
        }

        if (outcome.Reference is not null)
        {
            LogDeezerOnly(_logger, input.Raw, outcome.Reference.Id);

            return await DeezerOnlyAsync(outcome.Reference, cancellationToken).ConfigureAwait(false);
        }

        return Unresolved(input, $"No match on MusicBrainz or Deezer for '{input.Raw}'", BuildCandidates(outcome, ReviewCandidateLimit));
    }

    /// <summary>
    /// Steps 1 to 3 of the text pipeline: the Deezer search and its reference hit, the ISRC bridge and
    /// the MusicBrainz search.
    /// </summary>
    private async Task<TextOutcome> RunTextPipelineAsync(
        LookupInput input,
        ProviderTracker? tracker,
        CancellationToken cancellationToken)
    {
        var searchTerm = SearchTerm(input);
        IReadOnlyList<DeezerTrack> hits = (await GuardedAsync(
                ProviderKeys.Deezer,
                tracker,
                () => _deezer.SearchTracksAsync(searchTerm, SearchLimit, cancellationToken),
                new DeezerSearchResult(),
                cancellationToken)
            .ConfigureAwait(false)).Data;

        var query = QueryFromInput(input);
        var reference = query is null ? FindFreeTextReference(hits, input) : FindReference(hits, query);

        // Deezer's forgiving search still misses the album version when the artist name drags in other
        // artists (Calum Scott covers, say), so an artist-title lookup gets one retry on the title alone.
        if (reference is null && query is not null && input.Kind == LookupKind.ArtistTitle)
        {
            var retried = (await GuardedAsync(
                    ProviderKeys.Deezer,
                    tracker,
                    () => _deezer.SearchTracksAsync(query.Title, RetrySearchLimit, cancellationToken),
                    new DeezerSearchResult(),
                    cancellationToken)
                .ConfigureAwait(false)).Data;

            if (retried.Count > 0)
            {
                reference = FindReference(retried, query);
                hits = MergeHits(hits, retried);
            }
        }

        // For free text the reference hit supplies the artist and the title everything else is scored against.
        query ??= reference is null
            ? null
            : new TextQuery(
                reference.Artist.Name,
                BaseTitleOf(reference.TitleShort ?? reference.Title),
                FlagsOf(reference.TitleShort ?? reference.Title, null));

        LogSearch(_logger, searchTerm, hits.Count, reference?.Id);

        var referenceDurationMs = reference is null || reference.Duration <= 0 ? null : (int?)(reference.Duration * 1000);

        IReadOnlyList<MbRecording> viaIsrc = [];
        if (query is not null && !string.IsNullOrWhiteSpace(reference?.Isrc))
        {
            viaIsrc = await GuardedAsync(
                    ProviderKeys.MusicBrainz,
                    tracker,
                    () => _musicBrainz.GetRecordingsByIsrcAsync(reference!.Isrc!, cancellationToken),
                    [],
                    cancellationToken)
                .ConfigureAwait(false);

            var bridged = RankAcceptable(viaIsrc, query, referenceDurationMs, viaIsrc: true);
            if (bridged.Count > 0)
            {
                // The bridge answered: the caller stops here, so a typical line costs two MusicBrainz requests.
                return new TextOutcome(query, reference, referenceDurationMs, hits, viaIsrc, [], bridged);
            }
        }

        IReadOnlyList<MbRecording> searched = [];
        if (query is not null)
        {
            searched = await SearchRecordingsAsync(query, referenceDurationMs, tracker, cancellationToken)
                .ConfigureAwait(false);

            var ranked = RankAcceptable(searched, query, referenceDurationMs, viaIsrc: false);
            if (ranked.Count > 0)
            {
                return new TextOutcome(query, reference, referenceDurationMs, hits, viaIsrc, searched, ranked);
            }
        }

        return new TextOutcome(query, reference, referenceDurationMs, hits, viaIsrc, searched, []);
    }

    /// <summary>
    /// The MusicBrainz recording search. With a known reference length the duration-bounded query runs
    /// first, and the unbounded one only when the bounded one found no acceptable recording; when both
    /// run their results are merged, so the caller's candidates keep everything either search saw.
    /// </summary>
    private async Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(
        TextQuery query,
        int? referenceDurationMs,
        ProviderTracker? tracker,
        CancellationToken cancellationToken)
    {
        var unbounded = MusicBrainzQuery.RecordingByArtistAndTitle(query.Artist, query.Title);

        if (referenceDurationMs is null)
        {
            return await RunSearchAsync(unbounded, tracker, cancellationToken).ConfigureAwait(false);
        }

        var bounded = await RunSearchAsync(BoundedQuery(unbounded, referenceDurationMs.Value), tracker, cancellationToken)
            .ConfigureAwait(false);

        if (RankAcceptable(bounded, query, referenceDurationMs, viaIsrc: false).Count > 0)
        {
            return bounded;
        }

        var all = await RunSearchAsync(unbounded, tracker, cancellationToken).ConfigureAwait(false);

        return [.. bounded, .. all];
    }

    /// <summary>One MusicBrainz recording search.</summary>
    private async Task<IReadOnlyList<MbRecording>> RunSearchAsync(
        string luceneQuery,
        ProviderTracker? tracker,
        CancellationToken cancellationToken) =>
        (await GuardedAsync(
                ProviderKeys.MusicBrainz,
                tracker,
                () => _musicBrainz.SearchRecordingsAsync(luceneQuery, SearchLimit, cancellationToken),
                new MbRecordingSearchResult(),
                cancellationToken)
            .ConfigureAwait(false)).Recordings;

    /// <summary>
    /// Runs one provider call. Without a tracker a failure is the caller's problem; with one it is
    /// recorded, the provider is not asked again, and the fallback stands in for its answer.
    /// </summary>
    private async Task<T> GuardedAsync<T>(
        string provider,
        ProviderTracker? tracker,
        Func<Task<T>> call,
        T fallback,
        CancellationToken cancellationToken)
    {
        if (tracker is null)
        {
            return await call().ConfigureAwait(false);
        }

        if (tracker.Failed.Contains(provider))
        {
            return fallback;
        }

        try
        {
            var result = await call().ConfigureAwait(false);
            tracker.Succeeded.Add(provider);

            return result;
        }
        catch (Exception exception) when (ProviderKeys.IsProviderFailure(exception, cancellationToken))
        {
            LogProviderFailed(_logger, provider, exception.Message);
            tracker.Failed.Add(provider);
            tracker.FirstFailure ??= exception;

            return fallback;
        }
    }

    /// <summary>The Lucene query restricted to recordings within ten seconds of a reference length.</summary>
    private static string BoundedQuery(string luceneQuery, int referenceDurationMs)
    {
        var low = (referenceDurationMs - DurationAcceptanceMs).ToString(CultureInfo.InvariantCulture);
        var high = (referenceDurationMs + DurationAcceptanceMs).ToString(CultureInfo.InvariantCulture);

        return $"{luceneQuery} AND dur:[{low} TO {high}]";
    }

    /// <summary>The two Deezer search pages as one list, keeping the first search's order and no id twice.</summary>
    private static List<DeezerTrack> MergeHits(
        IReadOnlyList<DeezerTrack> first,
        IReadOnlyList<DeezerTrack> second)
    {
        var seen = new HashSet<long>();
        var merged = new List<DeezerTrack>(first.Count + second.Count);

        foreach (var hit in first.Concat(second))
        {
            if (seen.Add(hit.Id))
            {
                merged.Add(hit);
            }
        }

        return merged;
    }

    /// <summary>Reads the query's artist, title and flags out of an artist-title lookup.</summary>
    private static TextQuery? QueryFromInput(LookupInput input) =>
        input.Kind == LookupKind.ArtistTitle && input.Artist is not null && input.Title is not null
            ? new TextQuery(input.Artist, input.Title, FlagsOf(input.Title, null))
            : null;

    /// <summary>The text sent to the Deezer search: "artist title", or the free text as typed.</summary>
    private static string SearchTerm(LookupInput input) =>
        input.Kind == LookupKind.ArtistTitle ? $"{input.Artist} {input.Title}" : input.Raw;

    /// <summary>The first Deezer hit acceptable against the query, ignoring duration.</summary>
    private static DeezerTrack? FindReference(IReadOnlyList<DeezerTrack> hits, TextQuery query)
    {
        foreach (var hit in hits)
        {
            if (IsAcceptable(ScoreDeezerHit(query, hit, null), requireFlags: true))
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// The first Deezer hit whose hard flags match what the free text asks for: none, unless the text
    /// itself carries a version keyword.
    /// </summary>
    private static DeezerTrack? FindFreeTextReference(IReadOnlyList<DeezerTrack> hits, LookupInput input)
    {
        var expected = FlagsOf(input.Raw, null) & VersionFlagNames.HardFlags;

        foreach (var hit in hits)
        {
            if ((DeezerFlags(hit) & VersionFlagNames.HardFlags) == expected)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// Scores the MusicBrainz recordings and returns every acceptable one, best first: the highest
    /// score, and the earliest first release where two scores tie.
    /// </summary>
    private static IReadOnlyList<ScoredRecording> RankAcceptable(
        IReadOnlyList<MbRecording> recordings,
        TextQuery query,
        int? referenceDurationMs,
        bool viaIsrc)
    {
        var acceptable = new List<ScoredRecording>(recordings.Count);

        foreach (var recording in recordings)
        {
            var score = ScoreRecording(query, recording, referenceDurationMs, viaIsrc);

            if (IsAcceptable(score, requireFlags: true))
            {
                acceptable.Add(new ScoredRecording(recording, score));
            }
        }

        return
        [
            .. acceptable
                .OrderByDescending(scored => scored.Score.Total)
                .ThenBy(scored => scored.Recording.FirstReleaseDate ?? string.Empty, StringComparer.Ordinal),
        ];
    }

    /// <summary>The Deezer-only branch: one more identity read, no more searching.</summary>
    private async Task<ResolveResult> DeezerOnlyAsync(DeezerTrack track, CancellationToken cancellationToken)
    {
        var identity = await BuildDeezerIdentityAsync(track, cancellationToken).ConfigureAwait(false);

        return new ResolveResult { Status = ResolveStatus.ResolvedDeezerOnly, Identity = identity };
    }

    /// <summary>Bridges a Deezer track to MusicBrainz through its ISRC: every acceptable recording, best first.</summary>
    private async Task<IReadOnlyList<MbRecording>> BridgeToMusicBrainzAsync(
        TextQuery query,
        DeezerTrack track,
        int? referenceDurationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Isrc))
        {
            return [];
        }

        var recordings = await _musicBrainz
            .GetRecordingsByIsrcAsync(track.Isrc!, cancellationToken)
            .ConfigureAwait(false);

        return RecordingsOf(RankAcceptable(recordings, query, referenceDurationMs, viaIsrc: false));
    }

    /// <summary>
    /// Reads the identity of the best candidate that has an official release to file the song under. A
    /// recording whose only releases are bootlegs or promos answers with none, so the next acceptable
    /// candidate is tried — at most <see cref="IdentityAttempts"/> identity reads — and when none of them
    /// has an official release the first winner is kept rather than nothing at all.
    /// </summary>
    private async Task<IdentityPick?> IdentityWithReleasesAsync(
        IReadOnlyList<MbRecording> ranked,
        long? deezerTrackId,
        CancellationToken cancellationToken)
    {
        IdentityPick? first = null;
        var attempts = Math.Min(ranked.Count, IdentityAttempts);

        for (var rank = 0; rank < attempts; rank++)
        {
            var identity = await GetIdentityAsync(ranked[rank].Id, deezerTrackId, cancellationToken).ConfigureAwait(false);

            if (identity is null)
            {
                continue;
            }

            if (identity.ReleaseOptions.Count > 0)
            {
                return new IdentityPick(identity, rank);
            }

            first ??= new IdentityPick(identity, rank);
        }

        return first;
    }

    /// <summary>The recordings of a ranked candidate list, in the same order.</summary>
    private static IReadOnlyList<MbRecording> RecordingsOf(IReadOnlyList<ScoredRecording> ranked) =>
        [.. ranked.Select(scored => scored.Recording)];

    /// <summary>Builds a MusicBrainz identity from a recording and the releases it appears on.</summary>
    private static SongIdentity BuildMusicBrainzIdentity(
        MbRecording recording,
        IReadOnlyList<MbRelease> releases,
        long? deezerTrackId)
    {
        var options = new List<ReleaseOption>(releases.Count);

        foreach (var release in releases)
        {
            options.Add(ToReleaseOption(release));
        }

        return new SongIdentity
        {
            Source = MusicBrainzSource,
            MbRecordingId = recording.Id,
            DeezerId = deezerTrackId,
            Title = recording.Title,
            ArtistCredit = MbArtistCredit.Format(recording.ArtistCredit),
            Artists = BuildMusicBrainzArtists(recording.ArtistCredit),
            DurationMs = recording.Length,
            Isrcs = recording.Isrcs,
            Flags = FlagsOf(recording.Title, recording.Disambiguation),
            Disambiguation = NullIfEmpty(recording.Disambiguation),
            OriginalDate = NullIfEmpty(recording.FirstReleaseDate),
            ReleaseOptions = options,
        };
    }

    /// <summary>
    /// Maps an artist credit onto <see cref="IdentityArtist"/>: <see cref="Domain.ArtistRole.Main"/>
    /// until a join phrase with "feat", "ft.", "featuring" or "with" has been passed, Featured after.
    /// </summary>
    private static List<IdentityArtist> BuildMusicBrainzArtists(IReadOnlyList<MbArtistCredit> credits)
    {
        var artists = new List<IdentityArtist>(credits.Count);
        var featured = false;

        for (var i = 0; i < credits.Count; i++)
        {
            var credit = credits[i];

            artists.Add(new IdentityArtist(
                credit.Name,
                NullIfEmpty(credit.Artist.SortName),
                NullIfEmpty(credit.Artist.Id),
                null,
                featured ? Domain.ArtistRole.Featured : Domain.ArtistRole.Main,
                i));

            if (IsFeaturedJoin(credit.JoinPhrase))
            {
                featured = true;
            }
        }

        return artists;
    }

    /// <summary>Whether a join phrase hands the credit over to featured artists.</summary>
    private static bool IsFeaturedJoin(string? joinPhrase) =>
        joinPhrase is not null
        && (joinPhrase.Contains("feat", StringComparison.OrdinalIgnoreCase)
            || joinPhrase.Contains("ft.", StringComparison.OrdinalIgnoreCase)
            || joinPhrase.Contains("with", StringComparison.OrdinalIgnoreCase));

    /// <summary>Turns one MusicBrainz release into a release option (P1-05 picks between them).</summary>
    private static ReleaseOption ToReleaseOption(MbRelease release) =>
        new()
        {
            Key = release.Id,
            MbReleaseId = release.Id,
            MbReleaseGroupId = NullIfEmpty(release.ReleaseGroup?.Id),
            Title = release.Title,
            AlbumArtist = MbArtistCredit.Format(release.ArtistCredit),
            IsVariousArtists = release.ArtistCredit.Any(credit => credit.Artist.Id == VariousArtistsId),
            PrimaryType = NullIfEmpty(release.ReleaseGroup?.PrimaryType),
            SecondaryTypes = release.ReleaseGroup?.SecondaryTypes ?? [],
            Status = release.Status,
            Date = NullIfEmpty(release.Date),
            ReleaseGroupFirstDate = NullIfEmpty(release.ReleaseGroup?.FirstReleaseDate),

            // A browse returns the media but not their tracks; P1-07 reads the tracklist from the chosen release.
            TotalTracks = release.Media.Count == 0 ? null : release.Media.Sum(medium => medium.TrackCount),
            TrackNo = null,
            DiscNo = null,
        };

    /// <summary>Builds a Deezer-only identity: the track, its artists and its album as one release option.</summary>
    private async Task<SongIdentity> BuildDeezerIdentityAsync(DeezerTrack track, CancellationToken cancellationToken)
    {
        DeezerAlbum? album = null;
        if (track.Album.Id > 0)
        {
            album = await _deezer.GetAlbumAsync(track.Album.Id, cancellationToken).ConfigureAwait(false);
        }

        var artists = BuildDeezerArtists(track);
        var cover = NullIfEmpty(album?.CoverXl) ?? NullIfEmpty(track.Album.CoverXl);

        return new SongIdentity
        {
            Source = DeezerSource,
            DeezerId = track.Id,
            Title = track.Title,
            ArtistCredit = FormatDeezerCredit(artists),
            Artists = artists,
            DurationMs = track.Duration > 0 ? track.Duration * 1000 : null,
            Isrcs = IsrcsOf(track),
            Flags = DeezerFlags(track),
            OriginalDate = NullIfEmpty(album?.ReleaseDate) ?? NullIfEmpty(track.ReleaseDate),
            ReleaseOptions = album is null ? [] : [ToReleaseOption(track, album)],
            CoverUrl = cover,
        };
    }

    /// <summary>
    /// Maps Deezer's contributors onto <see cref="IdentityArtist"/>. Deezer's contributor JSON carries a
    /// role, but this model does not read it, so the track's own artist is the main one and the rest are
    /// featured; without a match the first contributor leads, as Deezer orders them.
    /// </summary>
    private static List<IdentityArtist> BuildDeezerArtists(DeezerTrack track)
    {
        if (track.Contributors.Count == 0)
        {
            return track.Artist.Id > 0
                ? [new IdentityArtist(track.Artist.Name, null, null, track.Artist.Id, Domain.ArtistRole.Main, 0)]
                : [];
        }

        var mainId = track.Artist.Id;
        var hasMain = mainId > 0 && track.Contributors.Any(contributor => contributor.Id == mainId);
        var artists = new List<IdentityArtist>(track.Contributors.Count);

        for (var i = 0; i < track.Contributors.Count; i++)
        {
            var contributor = track.Contributors[i];
            var isMain = hasMain ? contributor.Id == mainId : i == 0;

            artists.Add(new IdentityArtist(
                contributor.Name,
                null,
                null,
                contributor.Id > 0 ? contributor.Id : null,
                isMain ? Domain.ArtistRole.Main : Domain.ArtistRole.Featured,
                i));
        }

        return artists;
    }

    /// <summary>The display credit of a Deezer identity: the main artists, then "feat." the featured ones.</summary>
    private static string FormatDeezerCredit(IReadOnlyList<IdentityArtist> artists)
    {
        var main = artists.Where(artist => artist.Role == Domain.ArtistRole.Main).Select(artist => artist.Name).ToList();
        var featured = artists.Where(artist => artist.Role == Domain.ArtistRole.Featured).Select(artist => artist.Name).ToList();

        var credit = string.Join(", ", main);

        if (featured.Count == 0)
        {
            return credit;
        }

        var featuredCredit = $"feat. {string.Join(", ", featured)}";

        return credit.Length == 0 ? featuredCredit : $"{credit} {featuredCredit}";
    }

    /// <summary>Every artist name on a Deezer track, for scoring.</summary>
    private static List<string> DeezerArtistNames(DeezerTrack track)
    {
        var names = new List<string>(track.Contributors.Count + 1) { track.Artist.Name };

        foreach (var contributor in track.Contributors)
        {
            names.Add(contributor.Name);
        }

        return names;
    }

    /// <summary>Turns one Deezer album into a release option with a synthetic, deterministic key.</summary>
    private static ReleaseOption ToReleaseOption(DeezerTrack track, DeezerAlbum album)
    {
        var (primaryType, secondaryTypes) = album.RecordType?.ToLowerInvariant() switch
        {
            "album" => ("Album", (IReadOnlyList<string>)[]),
            "ep" => ("EP", (IReadOnlyList<string>)[]),
            "single" => ("Single", (IReadOnlyList<string>)[]),
            "compile" => ("Album", (IReadOnlyList<string>)["Compilation"]),
            _ => (null, (IReadOnlyList<string>)[]),
        };

        return new ReleaseOption
        {
            Key = DeterministicAlbumKey(album.Id),
            Title = album.Title,
            AlbumArtist = NullIfEmpty(album.Artist?.Name) ?? track.Artist.Name,
            PrimaryType = primaryType,
            SecondaryTypes = secondaryTypes,
            Status = "Official",
            Date = NullIfEmpty(album.ReleaseDate),
            TrackNo = track.TrackPosition,
            DiscNo = track.DiskNumber,
            TotalTracks = album.NbTracks,
            CoverUrl = NullIfEmpty(album.CoverMedium) ?? NullIfEmpty(album.CoverXl),
        };
    }

    /// <summary>
    /// The album key of a Deezer album: the MD5 of <c>deezer-album:{id}</c> as a GUID, so the same album
    /// always gets the same key.
    /// </summary>
    private static string DeterministicAlbumKey(long albumId)
    {
        // MD5 is not a security choice here: the digest only has to be stable across runs and processes.
#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"deezer-album:{albumId}"));
#pragma warning restore CA5351

        return new Guid(hash).ToString("D");
    }

    /// <summary>Ranks the candidates built by the text pipeline and cuts the list to <paramref name="limit"/>.</summary>
    private static IReadOnlyList<SongCandidate> Rank(IReadOnlyList<SongCandidate> candidates, int limit) =>
        [.. candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.FirstReleaseDate is null)
            .ThenBy(candidate => candidate.FirstReleaseDate, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.MbRecordingId is null)
            .ThenBy(candidate => candidate.MbRecordingId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DeezerId ?? 0)
            .Take(limit)];

    /// <summary>Builds the ranked candidates of a text pipeline run.</summary>
    private IReadOnlyList<SongCandidate> BuildCandidates(TextOutcome outcome, int limit)
    {
        if (outcome.Query is null)
        {
            return [];
        }

        var deezerCover = outcome.Reference?.Album.CoverXl;
        var byId = new Dictionary<string, SongCandidate>(StringComparer.Ordinal);
        var candidates = new List<SongCandidate>(outcome.Hits.Count + outcome.ViaIsrc.Count + outcome.Searched.Count);

        foreach (var recording in outcome.ViaIsrc)
        {
            // MusicBrainz' ISRC lookup does not echo the ISRCs back, and the Deezer track that led here
            // is the same recording: keep both so the UI can play a preview and dedupe against Deezer.
            var candidate = ToCandidate(outcome.Query, outcome.ReferenceDurationMs, recording, viaIsrc: true, deezerCover);
            var reference = outcome.Reference;
            if (reference is not null)
            {
                candidate = candidate with
                {
                    DeezerId = candidate.DeezerId ?? reference.Id,
                    Isrcs = candidate.Isrcs.Count > 0 || string.IsNullOrEmpty(reference.Isrc)
                        ? candidate.Isrcs
                        : [reference.Isrc.ToUpperInvariant()],
                };
            }

            Add(candidate);
        }

        foreach (var recording in outcome.Searched)
        {
            Add(ToCandidate(outcome.Query, outcome.ReferenceDurationMs, recording, viaIsrc: false, deezerCover));
        }

        foreach (var hit in outcome.Hits)
        {
            // The reference's ISRC already produced MusicBrainz candidates; every other hit is Deezer-only.
            if (outcome.Reference is not null && hit.Id == outcome.Reference.Id && outcome.ViaIsrc.Count > 0)
            {
                continue;
            }

            candidates.Add(ToDeezerCandidate(outcome.Query, outcome.ReferenceDurationMs, hit));
        }

        return Rank(candidates, limit);

        void Add(SongCandidate candidate)
        {
            var id = candidate.MbRecordingId!;

            if (!byId.TryGetValue(id, out var existing))
            {
                byId[id] = candidate;
                candidates.Add(candidate);
                return;
            }

            var keep = candidate.Score > existing.Score ? candidate : existing;
            var merged = keep with { ViaIsrc = candidate.ViaIsrc || existing.ViaIsrc };

            byId[id] = merged;
            candidates[candidates.IndexOf(existing)] = merged;
        }
    }

    /// <summary>Turns a MusicBrainz recording into a candidate.</summary>
    private SongCandidate ToCandidate(
        TextQuery query,
        int? referenceDurationMs,
        MbRecording recording,
        bool viaIsrc,
        string? deezerCoverUrl)
    {
        var release = recording.Releases.Count > 0 ? recording.Releases[0] : null;
        var group = release?.ReleaseGroup;

        return new SongCandidate
        {
            Source = MusicBrainzSource,
            MbRecordingId = recording.Id,
            Title = recording.Title,
            ArtistCredit = MbArtistCredit.Format(recording.ArtistCredit),
            DurationMs = recording.Length,
            Disambiguation = NullIfEmpty(recording.Disambiguation),
            Flags = FlagsOf(recording.Title, recording.Disambiguation),
            FirstReleaseDate = NullIfEmpty(recording.FirstReleaseDate),
            ReleaseTypes = ReleaseTypesOf(group),
            AlbumTitle = NullIfEmpty(release?.Title),
            ReleaseGroupId = NullIfEmpty(group?.Id),
            CoverUrl = viaIsrc && deezerCoverUrl is not null ? deezerCoverUrl : CoverArtUrl(group?.Id),
            Isrcs = recording.Isrcs,
            Score = ScoreRecording(query, recording, referenceDurationMs, viaIsrc).Total,
            ViaIsrc = viaIsrc,
        };
    }

    /// <summary>Turns a Deezer search hit into a Deezer-only candidate.</summary>
    private static SongCandidate ToDeezerCandidate(TextQuery query, int? referenceDurationMs, DeezerTrack hit)
    {
        var artists = BuildDeezerArtists(hit);

        return new SongCandidate
        {
            Source = DeezerSource,
            DeezerId = hit.Id,
            Title = hit.Title,
            ArtistCredit = FormatDeezerCredit(artists),
            DurationMs = hit.Duration > 0 ? hit.Duration * 1000 : null,
            Flags = DeezerFlags(hit),
            FirstReleaseDate = NullIfEmpty(hit.ReleaseDate),
            CoverUrl = NullIfEmpty(hit.Album.CoverXl),
            Isrcs = IsrcsOf(hit),
            Score = ScoreDeezerHit(query, hit, referenceDurationMs).Total,
        };
    }

    /// <summary>The distinct release types of a release group, for example <c>Album</c>.</summary>
    private static List<string> ReleaseTypesOf(MbReleaseGroup? group)
    {
        if (group is null)
        {
            return [];
        }

        var types = new List<string>();

        if (!string.IsNullOrWhiteSpace(group.PrimaryType))
        {
            types.Add(group.PrimaryType!);
        }

        foreach (var secondary in group.SecondaryTypes)
        {
            if (!types.Contains(secondary, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(secondary);
            }
        }

        return types;
    }

    /// <summary>The Cover Art Archive link for a release group; the UI shows a placeholder when it 404s.</summary>
    private string? CoverArtUrl(string? releaseGroupId)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
        {
            return null;
        }

        var baseUrl = _options.Value.CoverArtArchiveBaseUrl ?? string.Empty;
        if (baseUrl.Length > 0 && !baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return $"{baseUrl}release-group/{releaseGroupId}/front-250";
    }

    /// <summary>Scores a Deezer hit against the query.</summary>
    private static Score ScoreDeezerHit(TextQuery query, DeezerTrack hit, int? referenceDurationMs) =>
        ScoreCandidate(
            query,
            referenceDurationMs,
            hit.Title,
            null,
            FormatDeezerCredit(BuildDeezerArtists(hit)),
            DeezerArtistNames(hit),
            hit.Duration > 0 ? hit.Duration * 1000 : null,
            viaIsrc: false,
            isVideo: false);

    /// <summary>Scores a MusicBrainz recording against the query.</summary>
    private static Score ScoreRecording(
        TextQuery query,
        MbRecording recording,
        int? referenceDurationMs,
        bool viaIsrc)
    {
        var names = new List<string>(recording.ArtistCredit.Count);
        foreach (var credit in recording.ArtistCredit)
        {
            names.Add(credit.Name);
        }

        return ScoreCandidate(
            query,
            referenceDurationMs,
            recording.Title,
            recording.Disambiguation,
            MbArtistCredit.Format(recording.ArtistCredit),
            names,
            recording.Length,
            viaIsrc,
            recording.Video == true);
    }

    /// <summary>
    /// The candidate score from 0 to 100: the title and artist similarities, equal hard flags, the
    /// duration against the reference, the ISRC bridge bonus and the video penalty.
    /// </summary>
    private static Score ScoreCandidate(
        TextQuery query,
        int? referenceDurationMs,
        string candidateTitle,
        string? candidateDisambiguation,
        string candidateCredit,
        IReadOnlyList<string> candidateArtistNames,
        int? candidateLengthMs,
        bool viaIsrc,
        bool isVideo)
    {
        var queryInfo = VersionFlagParser.Parse(query.Title);
        var candidateFlags = FlagsOf(candidateTitle, candidateDisambiguation);

        var title = TextMatching.Similarity(queryInfo.BaseTitle, VersionFlagParser.Parse(candidateTitle).BaseTitle);
        var artist = ArtistSimilarity(query.Artist, candidateCredit, candidateArtistNames);
        var flagsEqual = (query.Flags & VersionFlagNames.HardFlags) == (candidateFlags & VersionFlagNames.HardFlags);

        var total = (title * TitleWeight)
            + (artist * ArtistWeight)
            + (flagsEqual ? FlagsScore : 0)
            + DurationTerm(referenceDurationMs, candidateLengthMs)
            + (viaIsrc ? IsrcBridgeBonus : 0)
            - (isVideo ? VideoPenalty : 0);

        return new Score(
            Math.Clamp(total, 0, 100),
            title,
            artist,
            flagsEqual,
            ReferenceDurationOk(referenceDurationMs, candidateLengthMs),
            candidateFlags);
    }

    /// <summary>
    /// Whether a candidate's length lets it be accepted. Always, unless a reference length is known and
    /// the candidate's own length differs from it by more than <see cref="DurationAcceptanceMs"/>. A
    /// candidate MusicBrainz gives no length for is judged on the other rules alone.
    /// </summary>
    private static bool ReferenceDurationOk(int? referenceDurationMs, int? candidateLengthMs) =>
        referenceDurationMs is null
        || candidateLengthMs is null
        || Math.Abs(referenceDurationMs.Value - candidateLengthMs.Value) <= DurationAcceptanceMs;

    /// <summary>The best artist similarity over the credit string and every credited artist.</summary>
    private static double ArtistSimilarity(string queryArtist, string credit, IReadOnlyList<string> names)
    {
        var normalizedQuery = TextMatching.NormalizeArtist(queryArtist);
        var best = TextMatching.Similarity(normalizedQuery, TextMatching.NormalizeArtist(credit));

        foreach (var name in names)
        {
            best = Math.Max(best, TextMatching.Similarity(normalizedQuery, TextMatching.NormalizeArtist(name)));
        }

        return best;
    }

    /// <summary>The duration term: full marks within two seconds of the reference, then a linear fall to zero.</summary>
    private static double DurationTerm(int? referenceDurationMs, int? candidateLengthMs)
    {
        if (candidateLengthMs is null)
        {
            return 0;
        }

        if (referenceDurationMs is null)
        {
            return DurationFallback;
        }

        var difference = Math.Abs(referenceDurationMs.Value - candidateLengthMs.Value);

        if (difference <= DurationToleranceMs)
        {
            return DurationScoreWeight;
        }

        return difference >= DurationCutoffMs
            ? 0
            : DurationScoreWeight * (1 - ((difference - DurationToleranceMs) / (double)(DurationCutoffMs - DurationToleranceMs)));
    }

    /// <summary>Whether a score clears every bar a candidate must clear to be accepted.</summary>
    private static bool IsAcceptable(Score score, bool requireFlags) =>
        score.Total >= AcceptScore
        && score.Title >= AcceptTitle
        && score.Artist >= AcceptArtist
        && score.DurationAcceptable
        && (!requireFlags || score.FlagsEqual);

    /// <summary>The version hints of a title, and of the disambiguation MusicBrainz adds to it.</summary>
    private static VersionFlags FlagsOf(string title, string? disambiguation) =>
        VersionFlagParser.Parse(title, disambiguation).Flags;

    /// <summary>The base title with every version hint and feat. clause removed.</summary>
    private static string BaseTitleOf(string title) => VersionFlagParser.Parse(title).BaseTitle;

    /// <summary>The flags a Deezer track carries: its title's hints, plus explicit when Deezer says so.</summary>
    private static VersionFlags DeezerFlags(DeezerTrack track) =>
        FlagsOf(track.Title, null) | (track.ExplicitLyrics ? VersionFlags.Explicit : VersionFlags.None);

    /// <summary>The upper-cased ISRCs of a Deezer track.</summary>
    private static IReadOnlyList<string> IsrcsOf(DeezerTrack track) =>
        string.IsNullOrWhiteSpace(track.Isrc) ? [] : [track.Isrc!.Trim().ToUpperInvariant()];

    /// <summary>Picks the recording a bare ISRC refers to: no hard flags, earliest first release.</summary>
    private static MbRecording? ChooseByIsrc(IReadOnlyList<MbRecording> recordings)
    {
        if (recordings.Count == 0)
        {
            return null;
        }

        MbRecording? best = null;
        var bestDate = string.Empty;

        foreach (var recording in recordings)
        {
            if ((FlagsOf(recording.Title, recording.Disambiguation) & VersionFlagNames.HardFlags) != VersionFlags.None)
            {
                continue;
            }

            var date = recording.FirstReleaseDate ?? string.Empty;

            if (best is null || string.CompareOrdinal(date, bestDate) < 0)
            {
                best = recording;
                bestDate = date;
            }
        }

        return best ?? recordings[0];
    }

    /// <summary>The query a Deezer track is scored against when it is the reference.</summary>
    private static TextQuery QueryFromDeezerTrack(DeezerTrack track) =>
        new(track.Artist.Name, track.Title, DeezerFlags(track));

    /// <summary>An unresolved result, with whatever candidates the lookup produced.</summary>
    private static ResolveResult Unresolved(LookupInput input, string reason, IReadOnlyList<SongCandidate>? candidates = null) =>
        new()
        {
            Status = ResolveStatus.Unresolved,
            Candidates = candidates ?? [],
            Reason = reason,
        };

    /// <summary>An empty string is the same as no value at all.</summary>
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The query everything in a text lookup is scored against.</summary>
    private sealed record TextQuery(string Artist, string Title, VersionFlags Flags);

    /// <summary>One candidate's score, and the parts that decided it.</summary>
    private sealed record Score(double Total, double Title, double Artist, bool FlagsEqual, bool DurationAcceptable, VersionFlags Flags);

    /// <summary>What the text pipeline found, before anything is turned into an identity.</summary>
    private sealed record TextOutcome(
        TextQuery? Query,
        DeezerTrack? Reference,
        int? ReferenceDurationMs,
        IReadOnlyList<DeezerTrack> Hits,
        IReadOnlyList<MbRecording> ViaIsrc,
        IReadOnlyList<MbRecording> Searched,
        IReadOnlyList<ScoredRecording> Ranked);

    /// <summary>Which providers answered and which did not, over one partial-tolerant search.</summary>
    private sealed class ProviderTracker
    {
        public List<string> Failed { get; } = [];

        public HashSet<string> Succeeded { get; } = [];

        public Exception? FirstFailure { get; set; }
    }

    /// <summary>A recording that cleared every bar, with the score that got it there.</summary>
    private sealed record ScoredRecording(MbRecording Recording, Score Score);

    /// <summary>The identity a resolve settled on, and the rank of the candidate behind it.</summary>
    private sealed record IdentityPick(SongIdentity Identity, int Rank);

    // Debug on purpose: a bulk add resolves hundreds of lines, and per-line Information would flood the log.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolving a {Kind} lookup for {Input}")]
    private static partial void LogResolving(ILogger logger, LookupKind kind, string input);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} did not answer the search, carrying on without it: {Reason}")]
    private static partial void LogProviderFailed(ILogger logger, string provider, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved {Input} to the recording {RecordingId}")]
    private static partial void LogResolvedRecording(ILogger logger, string input, string recordingId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved {Input} to the recording {RecordingId} scored {Score}")]
    private static partial void LogScored(ILogger logger, string input, string recordingId, double score);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved {Input} to the Deezer track {TrackId} only")]
    private static partial void LogDeezerOnly(ILogger logger, string input, long trackId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved the ISRC {Isrc} to the recording {RecordingId}")]
    private static partial void LogIsrcToRecording(ILogger logger, string isrc, string recordingId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved the ISRC {Isrc} to the Deezer track {TrackId}")]
    private static partial void LogIsrcToDeezer(ILogger logger, string isrc, long trackId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved the Deezer track {TrackId} to the recording {RecordingId}")]
    private static partial void LogDeezerToRecording(ILogger logger, long trackId, string recordingId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deezer search {Query} answered {HitCount} hits, reference {ReferenceId}")]
    private static partial void LogSearch(ILogger logger, string query, int hitCount, long? referenceId);
}
