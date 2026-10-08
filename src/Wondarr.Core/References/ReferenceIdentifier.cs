using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.References;

/// <summary>What one identification run over a reference library did.</summary>
/// <param name="Identified">How many files became owned songs.</param>
/// <param name="Ambiguous">How many files kept ranked candidates for the Match queue.</param>
/// <param name="Unmatched">How many files no tier found anything for.</param>
/// <param name="Deferred">How many files AcoustID could not be asked about this run; the next scan retries.</param>
public sealed record ReferenceIdentifyResult(int Identified, int Ambiguous, int Unmatched, int Deferred);

/// <summary>Turns the pending files of a reference library into owned songs (LIBRARY_OUTPUT §7.6).</summary>
public interface IReferenceIdentifier
{
    /// <summary>
    /// Identifies every <see cref="ReferenceFileState.Pending"/> file of one library: tag recording MBID,
    /// then ISRC, then AcoustID, then a title/artist search. Files at or above the auto-accept threshold
    /// become owned songs whose file is the one on disk; the rest get ranked candidates, or nothing.
    /// </summary>
    /// <param name="referenceLibraryId">The library whose pending files are identified.</param>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the run between files and chunks.</param>
    /// <returns>How the run's files ended.</returns>
    Task<ReferenceIdentifyResult> IdentifyPendingAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The identification pipeline (MATCHING_ENGINE.md §6.3, §6.5). Nothing here writes to the user's file:
/// an identified file is recorded as the song's file where it lies, and only the <c>song_file</c> row
/// Wondarr owns is ever removed.
/// </summary>
/// <remarks>
/// Work is done in chunks because the album policy plans a whole batch at once: adding one identity at
/// a time would scatter one artist's songs into a pseudo-album each.
/// </remarks>
public sealed partial class ReferenceIdentifier : IReferenceIdentifier
{
    /// <summary>How the file was identified from its own MusicBrainz recording tag.</summary>
    public const string TagMbIdTier = "tag_mbid";

    /// <summary>How the file was identified from its own ISRC tag.</summary>
    public const string IsrcTier = "isrc";

    /// <summary>How the file was identified from its acoustic fingerprint.</summary>
    public const string AcoustIdTier = "acoustid";

    /// <summary>How the file was identified from an artist and title search.</summary>
    public const string SearchTier = "search";

    /// <summary>The score of an ISRC or text match whose length could not confirm it.</summary>
    private const double UnconfirmedConfidence = 0.7;

    /// <summary>What a file's confidence counts as when the AcoustID score is not backed by its tags.</summary>
    private const double AcoustIdCap = 0.89;

    /// <summary>The confidence of a text search that resolved and whose length agrees.</summary>
    private const double SearchConfidence = 0.90;

    /// <summary>The confidence of an ISRC that bridged to one recording whose length agrees.</summary>
    private const double IsrcConfidence = 0.95;

    /// <summary>How many candidates one ambiguous file keeps, best first.</summary>
    private const int CandidateLimit = 5;

    /// <summary>The tag read and the probe are stored as camelCase JSON, the way the scan wrote them.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The separators that split an artist credit into the names it credits.</summary>
    private static readonly string[] CreditSeparators =
        [" feat. ", " feat ", " ft. ", " ft ", " featuring ", ", ", " & ", " and ", " x "];

    private readonly WondarrDbContext _database;
    private readonly IIdentityResolver _resolver;
    private readonly ISongService _songs;
    private readonly IFingerprinter _fingerprinter;
    private readonly IAcoustIdClient _acoustId;
    private readonly IOptionsMonitor<ReferenceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReferenceIdentifier> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReferenceIdentifier"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="resolver">Resolves tagged MBIDs, ISRCs and artist-title text.</param>
    /// <param name="songs">Adds the accepted identities as songs.</param>
    /// <param name="fingerprinter">Produces the Chromaprint fingerprints.</param>
    /// <param name="acoustId">Answers what recording a fingerprint belongs to.</param>
    /// <param name="options">The auto-accept threshold, the duration tolerance and the chunk size.</param>
    /// <param name="timeProvider">The clock the import timestamps come from.</param>
    /// <param name="logger">Logs one line per run and per refusal.</param>
    public ReferenceIdentifier(
        WondarrDbContext database,
        IIdentityResolver resolver,
        ISongService songs,
        IFingerprinter fingerprinter,
        IAcoustIdClient acoustId,
        IOptionsMonitor<ReferenceOptions> options,
        TimeProvider timeProvider,
        ILogger<ReferenceIdentifier> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(fingerprinter);
        ArgumentNullException.ThrowIfNull(acoustId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _resolver = resolver;
        _songs = songs;
        _fingerprinter = fingerprinter;
        _acoustId = acoustId;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReferenceIdentifyResult> IdentifyPendingAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken)
    {
        var library = await _database.ReferenceLibraries
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == referenceLibraryId, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            return new ReferenceIdentifyResult(0, 0, 0, 0);
        }

        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var addOptions = ReferenceOwnership.AddOptions(library);

        var pending = await _database.ReferenceFiles
            .Where(row => row.ReferenceLibraryId == referenceLibraryId
                && row.State == ReferenceFileState.Pending)
            .OrderBy(row => row.RelativePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var identified = 0;
        var ambiguous = 0;
        var unmatched = 0;
        var deferred = 0;
        var acoustIdDisabled = false;
        var acoustIdDown = false;
        var scanned = 0;

        for (var start = 0; start < pending.Count; start += options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = pending.GetRange(start, Math.Min(options.BatchSize, pending.Count - start));
            var planned = new List<PlannedRow>(chunk.Count);

            foreach (var row in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PlannedRow outcome;

                try
                {
                    outcome = await IdentifyRowAsync(
                            row,
                            library.RootPath,
                            options,
                            acoustIdDisabled,
                            acoustIdDown,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One file whose lookup threw (MusicBrainz down, a malformed answer) must not stop
                    // every file behind it in path order: it stays pending and the next scan retries it.
                    LogRowFailed(_logger, row.Id, exception);
                    outcome = Deferred(row, library.RootPath, "identification failed; the next scan retries this file", null);
                }

                acoustIdDisabled |= outcome.DisableAcoustId;
                acoustIdDown |= outcome.AcoustIdDown;
                planned.Add(outcome);
            }

            // One add per chunk: the album policy plans the batch as a whole.
            var accepted = planned.Where(row => row.State == RowState.Identified).ToList();
            var results = accepted.Count == 0
                ? []
                : await _songs
                    .AddIdentitiesAsync(
                        [.. accepted.Select(row => row.Identity!)],
                        addOptions,
                        cancellationToken)
                    .ConfigureAwait(false);

            for (var index = 0; index < accepted.Count; index++)
            {
                var row = accepted[index];
                var song = results[index].Song;

                await ReleaseAsync(row, song.Id).ConfigureAwait(false);

                var link = await ReferenceOwnership
                    .LinkAsync(
                        _database,
                        song.Id,
                        ReferenceOwnership.AbsolutePath(library.RootPath, row.Row.RelativePath),
                        row.Row,
                        row.Probe!,
                        row.AcoustId,
                        row.IdentifiedBy!,
                        now)
                    .ConfigureAwait(false);

                row.Row.SongId = song.Id;
                row.Row.State = ReferenceFileState.Identified;
                row.Row.Confidence = row.Confidence;
                row.Row.IdentifiedBy = row.IdentifiedBy;
                row.Row.AcoustId = row.AcoustId;
                row.Row.Fingerprint = row.Fingerprint;
                row.Row.Message = link.Message;

                await ReplaceCandidatesAsync(row, [], cancellationToken).ConfigureAwait(false);

                identified++;
            }

            foreach (var row in planned.Where(row => row.State != RowState.Identified))
            {
                // "We could not ask" says nothing about the file: a deferred row keeps the song it was
                // linked to, so that song is not wanted (and searched) again before the retry.
                if (row.State != RowState.Deferred)
                {
                    await ReleaseAsync(row, null).ConfigureAwait(false);

                    row.Row.SongId = null;
                }

                row.Row.AcoustId = row.AcoustId;
                row.Row.Fingerprint = row.Fingerprint;

                switch (row.State)
                {
                    case RowState.Ambiguous:
                        row.Row.State = ReferenceFileState.Ambiguous;
                        row.Row.Confidence = row.Confidence;
                        row.Row.IdentifiedBy = null;
                        row.Row.Message = null;

                        await ReplaceCandidatesAsync(row, row.Candidates, cancellationToken).ConfigureAwait(false);

                        ambiguous++;
                        break;

                    case RowState.Unmatched:
                        row.Row.State = ReferenceFileState.Unmatched;
                        row.Row.Confidence = 0;
                        row.Row.IdentifiedBy = null;
                        row.Row.Message = null;

                        await ReplaceCandidatesAsync(row, [], cancellationToken).ConfigureAwait(false);

                        unmatched++;
                        break;

                    default:
                        // Deferred: the row stays pending, carrying why, so the next scan retries it.
                        row.Row.State = ReferenceFileState.Pending;
                        row.Row.Confidence = 0;
                        row.Row.IdentifiedBy = null;
                        row.Row.Message = row.Message;

                        deferred++;
                        break;
                }
            }

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // The scan never touches a row twice, so the chunk leaves the change tracker at once.
            foreach (var row in chunk)
            {
                _database.Entry(row).State = EntityState.Detached;
            }

            scanned += chunk.Count;

            if (progress is not null)
            {
                await progress(string.Concat(
                        "Identified ",
                        scanned.ToString(CultureInfo.InvariantCulture),
                        " of ",
                        pending.Count.ToString(CultureInfo.InvariantCulture),
                        " pending files"))
                    .ConfigureAwait(false);
            }
        }

        var result = new ReferenceIdentifyResult(identified, ambiguous, unmatched, deferred);

        LogIdentified(_logger, referenceLibraryId, identified, ambiguous, unmatched, deferred);

        return result;
    }

    /// <summary>Frees the song a changed file used to be, when it is no longer the song the file is.</summary>
    private async Task ReleaseAsync(PlannedRow row, long? newSongId)
    {
        if (row.OldSongId is not { } oldSongId || oldSongId == newSongId)
        {
            return;
        }

        var path = ReferenceOwnership.AbsolutePath(row.RootPath, row.Row.RelativePath);

        if (await ReferenceOwnership.ReleaseAsync(_database, oldSongId, path).ConfigureAwait(false))
        {
            LogReleased(_logger, row.Row.Id, oldSongId);
        }
    }

    /// <summary>Replaces a row's candidates with this run's, ranked best first.</summary>
    private async Task ReplaceCandidatesAsync(
        PlannedRow row,
        IReadOnlyList<RankedCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var stale = await _database.MatchCandidates
            .Where(candidate => candidate.ReferenceFileId == row.Row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _database.MatchCandidates.RemoveRange(stale);

        var rank = 1;

        foreach (var candidate in candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Identity.Title, StringComparer.Ordinal)
            .Take(CandidateLimit))
        {
            _database.MatchCandidates.Add(new MatchCandidate
            {
                ReferenceFileId = row.Row.Id,
                Rank = rank++,
                Identity = candidate.Identity.ToJson(),
                Score = candidate.Score,
                Reason = candidate.Reason,
            });
        }
    }

    /// <summary>
    /// Decides one file's outcome: the first tier that yields an identity at or above the auto-accept
    /// threshold wins; everything else the lower tiers found is kept as a ranked candidate.
    /// </summary>
    private async Task<PlannedRow> IdentifyRowAsync(
        ReferenceFile row,
        string rootPath,
        ReferenceOptions options,
        bool acoustIdDisabled,
        bool acoustIdDown,
        CancellationToken cancellationToken)
    {
        var probe = Read<MediaInfo>(row.Probe);
        var tags = Plausible(Read<FileTags>(row.Tags));

        if (probe is null)
        {
            // A pending row is always probed; without a measurement there is nothing to identify against.
            return Unmatched(row, rootPath);
        }

        var candidates = new List<RankedCandidate>();
        string? fingerprint = null;
        string? acoustId = null;
        var disableAcoustId = false;

        // --- Tier 1: the recording MBID the file's own tags carry --------------------------------
        if (!string.IsNullOrWhiteSpace(tags?.MbRecordingId))
        {
            var identity = await _resolver
                .GetIdentityAsync(tags!.MbRecordingId, null, cancellationToken)
                .ConfigureAwait(false);

            if (identity is not null)
            {
                if (WithinTolerance(identity.DurationMs, probe.DurationMs, options.DurationToleranceMs))
                {
                    return Identified(
                        row,
                        rootPath,
                        identity,
                        1.0,
                        TagMbIdTier,
                        tags.AcoustId,
                        fingerprint,
                        disableAcoustId,
                        probe);
                }

                // A known MBID of the wrong length is more likely a mis-tagged file than another song,
                // so it stays as a weak candidate while the later tiers are still tried.
                var difference = Math.Abs(identity.DurationMs!.Value - probe.DurationMs);

                candidates.Add(new RankedCandidate(
                    MatchIdentity.From(identity),
                    0.5,
                    string.Concat("tagged MBID, length differs by ", Seconds(difference), " s")));
            }
        }

        // --- Tier 2: the ISRC the file's own tags carry -------------------------------------------
        if (!string.IsNullOrWhiteSpace(tags?.Isrc))
        {
            var resolved = await _resolver.ResolveAsync(tags!.Isrc!, cancellationToken).ConfigureAwait(false);

            if (resolved.Status == ResolveStatus.Resolved && resolved.Identity is { } isrcIdentity)
            {
                // Only a length both sides know can confirm an ISRC: one ISRC is reused across edits.
                if (LengthAgrees(isrcIdentity.DurationMs, probe.DurationMs, options.DurationToleranceMs)
                    && IsrcConfidence >= options.AutoAcceptThreshold)
                {
                    return Identified(
                        row,
                        rootPath,
                        isrcIdentity,
                        IsrcConfidence,
                        IsrcTier,
                        acoustId ?? tags.AcoustId,
                        fingerprint,
                        disableAcoustId,
                        probe);
                }

                candidates.Add(new RankedCandidate(
                    MatchIdentity.From(isrcIdentity),
                    UnconfirmedConfidence,
                    "ISRC, length unknown or differs"));
            }
        }

        // --- Tier 3: the acoustic fingerprint ------------------------------------------------------
        if (!acoustIdDisabled && acoustIdDown)
        {
            // AcoustID already said no earlier in this run: asking again per file only burns requests
            // and fingerprinting time, so the file waits for the next scan without a call.
            return Deferred(row, rootPath, "AcoustID unavailable; the next scan retries this file", null);
        }

        if (!acoustIdDisabled)
        {
            var printed = await _fingerprinter
                .FingerprintAsync(
                    ReferenceOwnership.AbsolutePath(rootPath, row.RelativePath),
                    FingerprintWindow.Start,
                    probe.DurationMs,
                    cancellationToken)
                .ConfigureAwait(false);

            if (printed.Success && printed.Fingerprint is not null)
            {
                fingerprint = printed.Fingerprint;

                var lookup = await _acoustId
                    .LookupAsync(printed.Fingerprint, printed.DurationSeconds, cancellationToken)
                    .ConfigureAwait(false);

                switch (lookup.Status)
                {
                    case AcoustIdStatus.Unavailable or AcoustIdStatus.RateLimited:
                        // "We could not ask" is not evidence the file is unidentifiable: the row stays
                        // pending and the next scan asks again.
                        return Deferred(row, rootPath, "AcoustID unavailable; the next scan retries this file", fingerprint)
                            with { AcoustIdDown = true };

                    case AcoustIdStatus.NotConfigured or AcoustIdStatus.InvalidKey:
                        // A configuration problem, not this file's: the tier is skipped for the whole
                        // run rather than fingerprinted and asked again for every file.
                        disableAcoustId = true;
                        LogAcoustIdDisabled(_logger, lookup.Status.ToString());

                        break;

                    case AcoustIdStatus.Ok:
                        var best = lookup.Results.OrderByDescending(result => result.Score).FirstOrDefault();

                        if (best is not null)
                        {
                            acoustId = best.Id;

                            var verdict = await AcoustIdVerdictAsync(best, tags, probe, options, cancellationToken)
                                .ConfigureAwait(false);

                            if (verdict.Accepted is not null)
                            {
                                return Identified(
                                    row,
                                    rootPath,
                                    verdict.Accepted,
                                    verdict.Confidence,
                                    AcoustIdTier,
                                    acoustId,
                                    fingerprint,
                                    disableAcoustId,
                                    probe);
                            }

                            candidates.AddRange(verdict.Candidates);
                        }

                        break;

                    default:
                        // A fingerprint the service could not read is this file's problem, and the
                        // text search below still gets its chance.
                        break;
                }
            }
        }

        // --- Tier 4: an artist and title search ----------------------------------------------------
        var (artist, title) = TextOf(tags, row.RelativePath);

        if (artist is not null && title is not null)
        {
            var query = string.Concat(artist, " - ", title);
            var resolved = await _resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);

            if (resolved.Status == ResolveStatus.Resolved
                && resolved.Identity is { } unconfirmed
                && !LengthAgrees(unconfirmed.DurationMs, probe.DurationMs, options.DurationToleranceMs))
            {
                // A text match whose length nobody knows (or that disagrees) is a suggestion, never an
                // automatic identification: the artist and title may have come from the file name.
                candidates.Add(new RankedCandidate(
                    MatchIdentity.From(unconfirmed),
                    UnconfirmedConfidence,
                    "text search, length unknown or differs"));
            }
            else if (resolved.Status == ResolveStatus.Resolved
                && resolved.Identity is { } searchIdentity)
            {
                if (SearchConfidence >= options.AutoAcceptThreshold)
                {
                    return Identified(
                        row,
                        rootPath,
                        searchIdentity,
                        SearchConfidence,
                        SearchTier,
                        acoustId ?? tags?.AcoustId,
                        fingerprint,
                        disableAcoustId,
                        probe);
                }

                candidates.Add(new RankedCandidate(
                    MatchIdentity.From(searchIdentity),
                    SearchConfidence,
                    "text search"));
            }

            foreach (var candidate in resolved.Candidates.Take(CandidateLimit))
            {
                candidates.Add(new RankedCandidate(
                    MatchIdentity.From(candidate),

                    // The resolver's score is 0–100; the confidence scale stops at 0.89 because only a
                    // fingerprint or an id-backed tier may auto-accept.
                    Math.Clamp(candidate.Score / 100 * AcoustIdCap, 0, AcoustIdCap),
                    string.Concat("search ", candidate.Score.ToString("0", CultureInfo.InvariantCulture))));
            }
        }

        return candidates.Count == 0
            ? Unmatched(row, rootPath, fingerprint)
            : Ambiguous(row, rootPath, candidates, fingerprint, acoustId, disableAcoustId);
    }

    /// <summary>
    /// Scores one AcoustID result: its own score, capped unless the file's tags (or the fact that the
    /// file has none and every recording agrees) confirm what it says.
    /// </summary>
    private async Task<AcoustIdVerdict> AcoustIdVerdictAsync(
        AcoustIdResult result,
        FileTags? tags,
        MediaInfo probe,
        ReferenceOptions options,
        CancellationToken cancellationToken)
    {
        var qualifying = result.Recordings
            .Where(recording => TagsConfirm(recording, tags) || NoTagsAndOneRecording(result, tags))
            .ToList();

        // Among several recordings the tags allow, the one whose own length is closest to what the
        // file measures is the one the file is.
        var chosen = qualifying.Count > 0
            ? qualifying.OrderBy(recording => Distance(recording, probe.DurationMs)).First()
            : result.Recordings.Count > 0 ? result.Recordings[0] : null;

        var confidence = qualifying.Count > 0 ? result.Score : Math.Min(result.Score, AcoustIdCap);

        var candidates = new List<RankedCandidate>();

        if (chosen is null)
        {
            return new AcoustIdVerdict(null, confidence, candidates);
        }

        var identity = await _resolver
            .GetIdentityAsync(chosen.Id, null, cancellationToken)
            .ConfigureAwait(false);

        // The fingerprint is taken from the start of the file, which an extended mix, a radio edit or
        // a live take can share with the album version: a length that disagrees caps the confidence,
        // whatever the tags say. An unknown length on both sides says nothing either way.
        var knownMs = identity?.DurationMs
            ?? (chosen.DurationSeconds is { } seconds ? (int)Math.Round(seconds * 1000) : null);
        var lengthDiffers = !WithinTolerance(knownMs, probe.DurationMs, options.DurationToleranceMs);

        if (lengthDiffers)
        {
            confidence = Math.Min(confidence, AcoustIdCap);
        }

        var reason = string.Concat(
            "AcoustID ",
            Score(confidence),
            qualifying.Count > 0 ? ", title matches" : string.Empty,
            lengthDiffers ? ", length differs" : string.Empty);

        foreach (var recording in result.Recordings.Take(CandidateLimit))
        {
            var recordingIdentity = string.Equals(recording.Id, chosen.Id, StringComparison.Ordinal)
                ? identity
                : await _resolver.GetIdentityAsync(recording.Id, null, cancellationToken).ConfigureAwait(false);

            if (recordingIdentity is null)
            {
                continue;
            }

            candidates.Add(new RankedCandidate(
                MatchIdentity.From(recordingIdentity),
                string.Equals(recording.Id, chosen.Id, StringComparison.Ordinal)
                    ? confidence
                    : Math.Min(result.Score, AcoustIdCap),
                reason));
        }

        return new AcoustIdVerdict(
            identity is not null && confidence >= options.AutoAcceptThreshold ? identity : null,
            confidence,
            candidates);
    }

    /// <summary>
    /// Whether the file's own tags confirm a recording AcoustID offered: the titles fold equal and one
    /// of the recording's artists folds equal to the tag's artist or to one of the artists it credits.
    /// </summary>
    private static bool TagsConfirm(AcoustIdRecording recording, FileTags? tags)
    {
        if (tags is null
            || string.IsNullOrWhiteSpace(tags.Title)
            || string.IsNullOrWhiteSpace(tags.Artist)
            || string.IsNullOrWhiteSpace(recording.Title))
        {
            return false;
        }

        if (!string.Equals(
                TextMatching.Normalize(recording.Title!),
                TextMatching.Normalize(tags.Title!),
                StringComparison.Ordinal))
        {
            return false;
        }

        var credited = CreditNames(tags.Artist!)
            .Select(TextMatching.NormalizeArtist)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return recording.ArtistNames.Any(artist => credited.Contains(TextMatching.NormalizeArtist(artist)));
    }

    /// <summary>
    /// Whether an untagged file's result speaks with one voice: every recording in it has the same
    /// folded title and first artist, so there is nothing for the tags to disagree with.
    /// </summary>
    private static bool NoTagsAndOneRecording(AcoustIdResult result, FileTags? tags)
    {
        if (result.Recordings.Count == 0)
        {
            return false;
        }

        if (tags is not null
            && (!string.IsNullOrWhiteSpace(tags.Title) || !string.IsNullOrWhiteSpace(tags.Artist)))
        {
            return false;
        }

        var first = Fold(result.Recordings[0]);

        return result.Recordings.All(recording => string.Equals(Fold(recording), first, StringComparison.Ordinal));
    }

    /// <summary>A recording's folded title and first artist, as one comparable string.</summary>
    private static string Fold(AcoustIdRecording recording) =>
        string.Concat(
            TextMatching.Normalize(recording.Title ?? string.Empty),
            "\u001f",
            recording.ArtistNames.Count > 0 ? TextMatching.NormalizeArtist(recording.ArtistNames[0]) : string.Empty);

    /// <summary>How far a recording's own length is from what the file measures; unknown sorts last.</summary>
    private static double Distance(AcoustIdRecording recording, int probedDurationMs) =>
        recording.DurationSeconds is { } seconds
            ? Math.Abs((seconds * 1000) - probedDurationMs)
            : double.MaxValue;

    /// <summary>
    /// The tags as identification reads them. Some taggers write the track number as the title and
    /// "Artist - Title" as the artist (every file of a real 96-file DJ set did); such a title says
    /// nothing, so it is dropped, and an artist holding both halves is split, so the fingerprint can
    /// be confirmed and the search asks for the right song. A number that may really be the title
    /// ("1999", "22") is kept: it is dropped only with a leading zero, when it equals the track number,
    /// or when the artist tag carries the title too.
    /// </summary>
    internal static FileTags? Plausible(FileTags? tags)
    {
        if (tags?.Title is not { } title || !NumberOnly().IsMatch(title))
        {
            return tags;
        }

        var artist = tags.Artist;
        var separator = string.IsNullOrWhiteSpace(artist) ? Match.Empty : ArtistTitleSeparator().Match(artist);
        var split = separator.Success && separator.Index > 0 && separator.Index + separator.Length < artist!.Length;
        var digits = title.Trim().TrimEnd('.', ')').Trim();
        var bogus = split
            || digits.StartsWith('0')
            || (tags.TrackNumber is { } track && string.Equals(
                digits,
                track.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));

        if (!bogus)
        {
            return tags;
        }

        return split
            ? tags with
            {
                Artist = artist![..separator.Index].Trim(),
                Title = artist[(separator.Index + separator.Length)..].Trim(),
            }
            : tags with { Title = null };
    }

    /// <summary>A title that is only a number, optionally followed by a dot or a bracket ("001", "7.").</summary>
    [GeneratedRegex(@"^\s*\d+\s*[.)]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberOnly();

    /// <summary>The dash between an artist and a title, with a space on at least one side ("A - T", "A- T").</summary>
    [GeneratedRegex(@"\s+-\s*|\s*-\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ArtistTitleSeparator();

    /// <summary>The artist and title to search with: the tags first, the path when they say nothing.</summary>
    private static (string? Artist, string? Title) TextOf(FileTags? tags, string relativePath)
    {
        var artist = string.IsNullOrWhiteSpace(tags?.Artist) ? null : tags!.Artist;
        var title = string.IsNullOrWhiteSpace(tags?.Title) ? null : tags!.Title;

        if (artist is not null && title is not null)
        {
            return (artist, title);
        }

        // The parser understands both "Artist/Album/01 - Title.ext" and "Artist - Title.ext".
        var parsed = SoulseekFilenameParser.Parse(relativePath).Parsed;

        return (artist ?? parsed.Artist, title ?? parsed.Title);
    }

    /// <summary>The names a credit string credits: the whole credit, and each part between its separators.</summary>
    private static IEnumerable<string> CreditNames(string credit)
    {
        yield return credit;

        foreach (var name in credit.Split(
            CreditSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return name;
        }
    }

    /// <summary>Whether both lengths are known and agree: what an ISRC or a text match needs to be accepted.</summary>
    private static bool LengthAgrees(int? knownMs, int probedMs, int toleranceMs) =>
        knownMs is not null
        && probedMs > 0
        && Math.Abs(knownMs.Value - probedMs) <= toleranceMs;

    /// <summary>Whether a known recording length lets the file be that recording.</summary>
    private static bool WithinTolerance(int? knownMs, int probedMs, int toleranceMs) =>
        knownMs is null
        || probedMs <= 0
        || Math.Abs(knownMs.Value - probedMs) <= toleranceMs;

    /// <summary>Whole seconds, for the lines a human reads.</summary>
    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000).ToString(CultureInfo.InvariantCulture);

    /// <summary>A score with at most two decimals, so "0.82" reads as the service sent it.</summary>
    private static string Score(double score) => score.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Reads one of the scan's stored JSON blobs, or <see langword="null"/> when there is none.</summary>
    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, StoredJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlannedRow Identified(
        ReferenceFile row,
        string rootPath,
        SongIdentity identity,
        double confidence,
        string identifiedBy,
        string? acoustId,
        string? fingerprint,
        bool disableAcoustId,
        MediaInfo probe) =>
        new(row, rootPath, probe, RowState.Identified, identity, confidence, identifiedBy, acoustId, fingerprint, [], null, disableAcoustId);

    private static PlannedRow Ambiguous(
        ReferenceFile row,
        string rootPath,
        IReadOnlyList<RankedCandidate> candidates,
        string? fingerprint,
        string? acoustId,
        bool disableAcoustId) =>
        new(
            row,
            rootPath,
            null,
            RowState.Ambiguous,
            null,
            candidates.Max(candidate => candidate.Score),
            null,
            acoustId,
            fingerprint,
            candidates,
            null,
            disableAcoustId);

    private static PlannedRow Unmatched(ReferenceFile row, string rootPath, string? fingerprint = null) =>
        new(row, rootPath, null, RowState.Unmatched, null, 0, null, null, fingerprint, [], null, false);

    private static PlannedRow Deferred(ReferenceFile row, string rootPath, string message, string? fingerprint) =>
        new(row, rootPath, null, RowState.Deferred, null, 0, null, null, fingerprint, [], message, false);

    /// <summary>How one file's identification ended, before anything is written.</summary>
    private enum RowState
    {
        /// <summary>An identity at or above the threshold; the file becomes the song's file.</summary>
        Identified,

        /// <summary>Ranked candidates, none of them good enough to accept on its own.</summary>
        Ambiguous,

        /// <summary>Nothing found.</summary>
        Unmatched,

        /// <summary>AcoustID could not be asked; the row stays pending.</summary>
        Deferred,
    }

    /// <summary>One file's plan: what it is, how sure, and the candidates to store when it is not accepted.</summary>
    private sealed record PlannedRow(
        ReferenceFile Row,
        string RootPath,
        MediaInfo? Probe,
        RowState State,
        SongIdentity? Identity,
        double Confidence,
        string? IdentifiedBy,
        string? AcoustId,
        string? Fingerprint,
        IReadOnlyList<RankedCandidate> Candidates,
        string? Message,
        bool DisableAcoustId)
    {
        /// <summary>Gets a value indicating whether AcoustID refused this run (rate limit, outage).</summary>
        public bool AcoustIdDown { get; init; }

        /// <summary>The song the scanner kept on the row, which the file may no longer be.</summary>
        public long? OldSongId { get; init; } = Row.SongId;
    }

    /// <summary>One candidate for the Match queue.</summary>
    private sealed record RankedCandidate(MatchIdentity Identity, double Score, string Reason);

    /// <summary>What one AcoustID result says about the file.</summary>
    private sealed record AcoustIdVerdict(
        SongIdentity? Accepted,
        double Confidence,
        IReadOnlyList<RankedCandidate> Candidates);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Identified reference library {ReferenceLibraryId}: {Identified} identified, {Ambiguous} ambiguous, {Unmatched} unmatched, {Deferred} deferred")]
    private static partial void LogIdentified(
        ILogger logger,
        long referenceLibraryId,
        int identified,
        int ambiguous,
        int unmatched,
        int deferred);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reference file {ReferenceFileId} is no longer song {SongId}; the song is wanted again")]
    private static partial void LogReleased(ILogger logger, long referenceFileId, long songId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AcoustID is unusable ({Status}); the fingerprint tier is skipped for this run")]
    private static partial void LogAcoustIdDisabled(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identifying reference file {ReferenceFileId} failed; it stays pending")]
    private static partial void LogRowFailed(ILogger logger, long referenceFileId, Exception exception);
}
