using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Messaging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Verification;

namespace Wondarr.Core.Importing;

/// <summary>How one <see cref="IImportService.ImportAsync"/> call ended.</summary>
public enum ImportOutcome
{
    /// <summary>The song's first file was imported.</summary>
    Imported,

    /// <summary>A better file replaced the one the song already had.</summary>
    Upgraded,

    /// <summary>The file was not the wanted recording; it is blocklisted for this song.</summary>
    Rejected,

    /// <summary>The verdict could not be reached (AcoustID is down); the item is left for a later check.</summary>
    Deferred,

    /// <summary>The import failed on our side: the file stays where it is.</summary>
    Failed,

    /// <summary>The item is not in a state the import can work on.</summary>
    NotReady,
}

/// <summary>Turns a finished download into a library file, or refuses it.</summary>
public interface IImportService
{
    /// <summary>
    /// Verifies, tags, names, places and records one finished download (ARCHITECTURE §5.2 steps 8–11).
    /// </summary>
    /// <param name="queueItemId">The queue item whose download is complete.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What became of the file.</returns>
    Task<ImportOutcome> ImportAsync(long queueItemId, CancellationToken cancellationToken);
}

/// <summary>
/// The import pipeline (ARCHITECTURE §5.2 steps 8–11, MATCHING_ENGINE §6.5): it verifies the downloaded
/// file is the wanted recording, checks its measured quality against the profile, writes the full tag
/// set with the cover, renders its path from the library's template, places it over the file it
/// upgrades, and records the result.
/// </summary>
/// <remarks>
/// A refused file is blocklisted for this song, the peer's reputation drops, the download is deleted
/// and the next accepted candidate of the same search is grabbed. A file that cannot be judged because
/// AcoustID is down is deferred, never imported unverified. The item is loaded tracked and written back
/// through the same context, so a state change is a state change (<see cref="IQueueService"/>'s
/// rule about detached items does not apply here).
/// </remarks>
public sealed partial class ImportService : IImportService
{
    /// <summary>The history value written when no further candidate exists to try.</summary>
    internal const string NoMoreCandidates = "no more candidates";

    /// <summary>How long a deferred file waits before the import is attempted again.</summary>
    private static readonly TimeSpan DeferralDelay = TimeSpan.FromMinutes(15);

    /// <summary>How long an import waits while a compaction has the song's file in its staging folder.</summary>
    private static readonly TimeSpan CompactionWait = TimeSpan.FromMinutes(1);

    /// <summary>What a compaction-deferred item says, as the user reads it.</summary>
    internal const string CompactionWaitMessage = "Waiting for a library compaction to finish with this song";

    /// <summary>How many failures are kept per peer; the same number <see cref="SoulseekUserService"/> keeps.</summary>
    private const int MaxRecentFailures = 10;

    /// <summary>
    /// The only extensions the import will write. Anything else — <c>.webm</c> above all (ADR-0006) —
    /// is refused before a single byte of the file is touched.
    /// </summary>
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.Ordinal)
    {
        "mp3", "flac", "m4a", "aac", "ogg", "oga", "opus", "wav", "aif", "aiff", "ape", "wv", "wma", "alac",
    };

    /// <summary>The separators a path may use here, so a path written on either platform is read alike.</summary>
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The JSON shape of every payload this class writes: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WondarrDbContext _database;
    private readonly IDownloadVerifier _verifier;
    private readonly ITranscoder _transcoder;
    private readonly IMediaProbe _probe;
    private readonly ISpectralAnalyzer _spectral;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly ILibraryOrganizer _organizer;
    private readonly ISongSearchService _search;
    private readonly IEventAggregator _events;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ISongFileLock _songFileLock;
    private readonly ILogger<ImportService> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportService"/> class.</summary>
    /// <param name="database">The Wondarr database; the item, the song and the file are written through it.</param>
    /// <param name="verifier">Decides whether the file is the wanted recording.</param>
    /// <param name="transcoder">Turns a download into the library's output policy target.</param>
    /// <param name="probe">Measures what a downloaded file really is, so the right rule is applied.</param>
    /// <param name="spectral">Looks for a lossy encoder's low-pass in a lossless download.</param>
    /// <param name="importOptions">Whether the fake-lossless check runs.</param>
    /// <param name="organizer">Tags, names and places the file, recycling what it replaces.</param>
    /// <param name="search">Grabs the next candidate when a file is refused.</param>
    /// <param name="events">Publishes the queue and import events.</param>
    /// <param name="options">The attempt budget a rejected file works within.</param>
    /// <param name="timeProvider">The clock every timestamp comes from.</param>
    /// <param name="songFileLock">The per-song lock the compaction's stage step also takes.</param>
    /// <param name="logger">The logger.</param>
    public ImportService(
        WondarrDbContext database,
        IDownloadVerifier verifier,
        ITranscoder transcoder,
        IMediaProbe probe,
        ISpectralAnalyzer spectral,
        IOptionsMonitor<ImportOptions> importOptions,
        ILibraryOrganizer organizer,
        ISongSearchService search,
        IEventAggregator events,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ISongFileLock songFileLock,
        ILogger<ImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(transcoder);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(spectral);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(songFileLock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _verifier = verifier;
        _transcoder = transcoder;
        _probe = probe;
        _spectral = spectral;
        _importOptions = importOptions;
        _organizer = organizer;
        _search = search;
        _events = events;
        _options = options;
        _timeProvider = timeProvider;
        _songFileLock = songFileLock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ImportOutcome> ImportAsync(long queueItemId, CancellationToken cancellationToken)
    {
        // Tracked on purpose: every state change below is written through this context.
        var item = await _database.QueueItems
            .Include(entry => entry.Candidate)
            .FirstOrDefaultAsync(entry => entry.Id == queueItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            LogMissingItem(_logger, queueItemId);

            return ImportOutcome.NotReady;
        }

        try
        {
            return await ImportCoreAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Something Wondarr did not foresee. The file stays where it is: an import that failed
            // halfway through is a bug to look at, not evidence that the download is wrong. The
            // caller still gets an outcome — recording the failure is not allowed to throw either.
            LogImportFailed(_logger, item.Id, exception);

            return await FailSafelyAsync(item.Id, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The pipeline itself, with the item already loaded.</summary>
    private async Task<ImportOutcome> ImportCoreAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var song = await LoadSongAsync(item.SongId, cancellationToken).ConfigureAwait(false);
        var album = song?.AlbumContext;

        // Nothing to work on yet: the poll moves the item to Completed once the source reports the
        // file done, and a song with no album context has no folder or tags to be filed under.
        if (song is null || album is null || item.State != QueueItemState.Completed)
        {
            return ImportOutcome.NotReady;
        }

        var downloadPath = item.DownloadPath;

        if (string.IsNullOrWhiteSpace(downloadPath))
        {
            return ImportOutcome.NotReady;
        }

        var profile = await _database.QualityProfiles
            .FirstOrDefaultAsync(candidate => candidate.Id == song.QualityProfileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat(
                    "The song's quality profile ",
                    song.QualityProfileId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        var library = await _database.Libraries
            .FirstOrDefaultAsync(candidate => candidate.Id == song.LibraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat(
                    "The song's library ",
                    song.LibraryId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        if (!File.Exists(downloadPath))
        {
            LogDownloadMissing(_logger, item.Id, downloadPath);

            await FailAsync(
                    item,
                    $"Downloaded file is missing: {downloadPath}",
                    null,
                    null,
                    allowNextAttempt: true,
                    cancellationToken)
                .ConfigureAwait(false);

            return ImportOutcome.Failed;
        }

        // --- Compaction guard (early) -------------------------------------------------------------
        // A compaction with this song's file parked in its staging folder must finish before the
        // import touches the song: the import's place-and-record step would otherwise rewrite the
        // song's file row while the compaction's move back files the old staged file over it. The
        // item stays Completed and the queue poll brings it back; nothing is counted, blocklisted
        // or written to the history.
        if (await HasUnfinishedCompactionMoveAsync(song.Id, cancellationToken).ConfigureAwait(false))
        {
            return await DeferForCompactionAsync(item, downloadPath, cancellationToken).ConfigureAwait(false);
        }

        item.State = QueueItemState.Importing;
        item.StateChangedAt = now;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);

        // --- Extension --------------------------------------------------------------------------
        // What the file claims to be decides whether it may be imported at all, and it is settled
        // before anything is written to it or asked about it: ADR-0006 — Wondarr never writes .webm,
        // and a name with no audio extension is not a file we will hand to the library.
        var extension = Path.GetExtension(downloadPath).TrimStart('.').ToLowerInvariant();

        if (extension.Length == 0)
        {
            return await RejectAsync(
                    item,
                    song,
                    "The download has no audio file extension",
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!ImportableExtensions.Contains(extension))
        {
            LogExtensionRefused(_logger, item.Id, extension);

            return await RejectAsync(
                    item,
                    song,
                    $"Refusing to import .{extension} files",
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // --- Song file lock -----------------------------------------------------------------------
        // The same per-song lock the compaction's stage step takes: from here to the record step,
        // no compaction can stage this song's file, and no import can act on it either. The lock
        // spans the transcode and the verification on purpose: the re-check further down — made
        // before anything is placed or recorded — then always wins the race against a compaction
        // that stages while the import is in flight, instead of the two writing the song's file
        // row over each other. The verification's AcoustID lookup is the one network call this
        // holds the lock across, and it is bounded by the client's own timeout.
        await using var songFileLock = await _songFileLock
            .AcquireAsync(song.Id, cancellationToken)
            .ConfigureAwait(false);

        // --- Convert (the library's output policy) ------------------------------------------------
        // Every download is converted per the library's output policy: one rule per source class —
        // youtube, lossy, lossless (ADR-0008). A YouTube download is the lossless remux of itag 251
        // (.opus); a file from any other source is probed, and the rule its class names is applied.
        // Whatever the conversion writes, the file is ranked as the quality that was downloaded.
        var importPath = downloadPath;
        long? sourceQualityId = null;

        // The temporary file a non-YouTube conversion is written to: deleted again on every exit
        // but the one that places it, so a .wondarr-convert. file is never left behind.
        using var convertTemp = new ConvertTempFile();

        var policy = LibraryOutputPolicy.Parse(library.OutputPolicy);

        if (item.SourceType == SourceTypes.YouTube)
        {
            // The file is ranked as the Opus stream it was downloaded as, whatever the rule turns
            // it into: a 256 kbps AAC made from it is still a YouTube grab.
            sourceQualityId = SeedData.Opus160QualityId;

            var rule = policy.YouTube;

            if (rule.Codec is OutputCodec.Keep or OutputCodec.Opus)
            {
                // Keeping the Opus remux — or re-encoding it as Opus, which is the same codec — is
                // no transcode at all. An .opus file is already an Ogg container, so a rule that
                // names the ogg container only renames the file: same bytes, never a re-encode.
                if (rule.OpusContainer == "ogg" && extension == "opus")
                {
                    var renamed = Path.Combine(
                        Path.GetDirectoryName(downloadPath) ?? string.Empty,
                        string.Concat(Path.GetFileNameWithoutExtension(downloadPath), ".ogg"));

                    try
                    {
                        // A leftover .ogg from an attempt that stopped half-way would make the move
                        // refuse: it is the same bytes, so the fresh remux replaces it.
                        File.Move(downloadPath, renamed, overwrite: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Like a failed transcode: our own step, nothing to blocklist, and the remux
                        // stays where it was for a later attempt.
                        LogTranscodeFailed(_logger, item.Id, exception);

                        await FailAsync(
                                item,
                                $"Renaming the Opus remux to .ogg failed: {exception.Message}",
                                null,
                                null,
                                allowNextAttempt: false,
                                cancellationToken)
                            .ConfigureAwait(false);

                        return ImportOutcome.Failed;
                    }

                    importPath = renamed;
                    extension = "ogg";

                    // The renamed file is the download from here on: a rejection deletes it, and a
                    // deferred import finds it (and renames nothing) when the poll brings it back.
                    item.DownloadPath = renamed;
                }
            }
            else if (extension != rule.Container)
            {
                // (A download path that already holds the rule's container is a file this import
                // transcoded on an earlier pass and then deferred: transcoding it again would mean
                // copying a file over itself, so the step is skipped and the file is imported as it
                // is — which is what the rule wanted the first time around.)
                var destination = Path.Combine(
                    Path.GetDirectoryName(downloadPath) ?? string.Empty,
                    string.Concat(item.Candidate.RemotePath, ".", rule.Container));

                try
                {
                    var transcoded = await _transcoder
                        .TranscodeAsync(downloadPath, rule, destination, sourceIsLossless: false, cancellationToken)
                        .ConfigureAwait(false);

                    importPath = transcoded.Path;

                    // The Opus original is deleted only after the transcode succeeded: it is the
                    // source the new file was made from, and a failed transcode must be able to run
                    // again. A delete that cannot run is the transcode's failure, not the import's:
                    // both files on disk would make the next attempt hit an existing destination.
                    File.Delete(downloadPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The transcode is our own step, not the download's: the file passed nothing yet,
                    // so there is nothing to blocklist and no reason to grab the next candidate —
                    // the same rule as a tagging failure. The Opus original stays on disk, so a
                    // later import can try the step again.
                    LogTranscodeFailed(_logger, item.Id, exception);

                    await FailAsync(
                            item,
                            $"Transcode failed: {exception.Message}",
                            null,
                            null,
                            allowNextAttempt: false,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return ImportOutcome.Failed;
                }

                extension = Path.GetExtension(importPath).TrimStart('.').ToLowerInvariant();
            }
        }
        else
        {
            // What the file really is decides which rule applies — and whether one applies at all:
            // a file that does not decode is left as it is, and the verifier fails it as today.
            var probed = await _probe.ProbeAsync(downloadPath, cancellationToken).ConfigureAwait(false);

            if (probed.Decodable && probed.Info is { } info)
            {
                // A lossless container from a lossy source is refused before anything converts it
                // (ADR-0006; DECISIONS build session 10 #2). Inconclusive and Genuine carry on.
                if (info.IsLossless && _importOptions.CurrentValue.FakeLosslessCheck == FakeLosslessCheck.Reject)
                {
                    var verdict = await _spectral
                        .AnalyzeAsync(downloadPath, info.DurationMs, cancellationToken)
                        .ConfigureAwait(false);

                    if (verdict is { Outcome: SpectralOutcome.Lossy, CutoffHz: { } cutoff })
                    {
                        var reason = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Fake lossless: the spectrum stops at {cutoff / 1000:0.0} kHz (a lossy source)");

                        LogFakeLossless(_logger, item.Id, cutoff);

                        // The verifier was never asked: the history's verification slot stays empty.
                        return await RejectAsync(
                                item,
                                song,
                                reason,
                                null,
                                MeasuredQuality.FromMediaInfo(info),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    LogSpectralVerdict(_logger, item.Id, verdict.Outcome, verdict.CutoffHz);
                }

                var rule = policy.RuleFor(item.SourceType, info.IsLossless);

                // A file the rule keeps sets no source quality: the verifier measures it, and what
                // it measures is what was downloaded. Only a converted file needs the probe's answer
                // carried past the conversion.
                if (rule.Codec != OutputCodec.Keep && !LibraryOutputPolicy.SameCodec(rule, info.Codec))
                {
                    // The file is ranked as what was downloaded, not what the conversion writes:
                    // a FLAC converted to MP3-320 is still a FLAC grab, so the profile gate, the
                    // upgrade check and song_file.quality_id all see the FLAC.
                    sourceQualityId = MeasuredQuality.FromMediaInfo(info);

                    convertTemp.Path = Path.Combine(
                        Path.GetDirectoryName(downloadPath) ?? string.Empty,
                        string.Concat(
                            Path.GetFileNameWithoutExtension(downloadPath),
                            ".wondarr-convert.",
                            rule.Container));

                    // A leftover from an earlier attempt would make the encoder refuse to write.
                    convertTemp.Delete();

                    try
                    {
                        var transcoded = await _transcoder
                            .TranscodeAsync(downloadPath, rule, convertTemp.Path, info.IsLossless, cancellationToken)
                            .ConfigureAwait(false);

                        importPath = transcoded.Path;
                        extension = rule.Container;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // As for a YouTube transcode: the file passed nothing yet, so there is
                        // nothing to blocklist and no reason to grab the next candidate. The
                        // downloaded original stays on disk, so a later import can try again.
                        LogTranscodeFailed(_logger, item.Id, exception);

                        await FailAsync(
                                item,
                                $"Transcode failed: {exception.Message}",
                                null,
                                null,
                                allowNextAttempt: false,
                                cancellationToken)
                            .ConfigureAwait(false);

                        return ImportOutcome.Failed;
                    }
                }
            }
        }

        // --- Verify -----------------------------------------------------------------------------
        var credits = song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => (credit.Artist, credit.Role))
            .ToList();

        var verification = await _verifier
            .VerifyAsync(
                new VerificationRequest(
                    importPath,
                    song.MbRecordingId,
                    song.Title,
                    [.. credits.Where(credit => credit.Role == ArtistRole.Main).Select(credit => credit.Artist.Name)],
                    song.DurationMs,
                    ParseFlags(song.VersionFlags),
                    profile.DurationToleranceMs,
                    sourceQualityId),
                cancellationToken)
            .ConfigureAwait(false);

        if (verification.Outcome == VerificationOutcome.Deferred)
        {
            // "We could not ask" is not evidence that the file is right: the item goes back to the
            // queue and the poll brings it here again.
            item.State = QueueItemState.Completed;
            item.StateChangedAt = now;
            item.NextCheckAt = now + DeferralDelay;
            item.Message = verification.Reason;
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);

            LogDeferred(_logger, item.Id, verification.Reason);

            return ImportOutcome.Deferred;
        }

        if (verification.Outcome == VerificationOutcome.Failed || verification.Media is null)
        {
            return await RejectAsync(
                    item,
                    song,
                    verification.Reason,
                    verification.Reason,
                    verification.MeasuredQualityId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var media = verification.Media;
        var measured = verification.MeasuredQualityId ?? SoulseekQuality.Unknown;
        var needsReview = verification.Outcome == VerificationOutcome.NeedsReview;
        var fingerprintVerified = verification.Outcome == VerificationOutcome.Passed
            && verification.FingerprintVerified;

        // The recording id the file taught us is only *decided* here. It is written to the song in the
        // record step, and only when the file is imported: a file that is refused must not rename the
        // song's identity (and a rejection never saves the song).
        var learnedMbId = await DecideRecordingIdAsync(song, verification, cancellationToken)
            .ConfigureAwait(false);

        // --- Quality ----------------------------------------------------------------------------
        var measuredQuality = await _database.Qualities
            .AsNoTracking()
            .FirstOrDefaultAsync(quality => quality.Id == measured, cancellationToken)
            .ConfigureAwait(false);

        var measuredName = measuredQuality?.Name ?? measured.ToString(CultureInfo.InvariantCulture);

        if (!profile.IsAllowed(measured) || measuredQuality is null)
        {
            // A "FLAC" that is really a lossy file lands here: what matters is what ffprobe measured,
            // not what the file name claimed.
            return await RejectAsync(
                    item,
                    song,
                    $"Measured quality {measuredName} is not allowed by {profile.Name}",
                    verification.Reason,
                    measured,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // An automatic grab only ever wins when it is a genuine upgrade; a manual grab is the user's
        // own decision and replaces whatever the song held.
        if (song.File is { } held
            && await IsAutomaticGrabAsync(item, cancellationToken).ConfigureAwait(false)
            && !profile.IsUpgrade(held.QualityId, measured))
        {
            var currentQuality = await _database.Qualities
                .AsNoTracking()
                .FirstOrDefaultAsync(quality => quality.Id == held.QualityId, cancellationToken)
                .ConfigureAwait(false);

            return await RejectAsync(
                    item,
                    song,
                    $"Not an upgrade: {measuredName} vs {currentQuality?.Name ?? held.QualityId.ToString(CultureInfo.InvariantCulture)}",
                    verification.Reason,
                    measured,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // A first import may rest on the probe and the length when AcoustID does not know a file —
        // better than nothing. Replacing a file the song already has needs more: an automatic upgrade
        // must be confirmed by its fingerprint (DECISIONS build session 6 #9). Seen live on 2026-10-07:
        // "Paint It Black (Chris Farlowe)" from a Rolling Stones box-set folder, unknown to AcoustID,
        // passed on its length and replaced the Rolling Stones' recording.
        if (song.File is not null
            && !fingerprintVerified
            && await IsAutomaticGrabAsync(item, cancellationToken).ConfigureAwait(false))
        {
            return await RejectAsync(
                    item,
                    song,
                    "Not an upgrade: the replacement could not be confirmed by its fingerprint, and an automatic "
                    + "upgrade never replaces a file with one it cannot confirm",
                    verification.Reason,
                    measured,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // --- Compaction guard (re-check) ---------------------------------------------------------
        // A compaction may have planned or staged since the early check above — while this import
        // was converting and verifying, most often, which is why this sits inside the song lock.
        // The item is Importing now, so it goes back to Completed and the poll brings it back;
        // the downloaded (and, if it got that far, transcoded) file is left exactly where it is.
        if (await HasUnfinishedCompactionMoveAsync(song.Id, cancellationToken).ConfigureAwait(false))
        {
            // A converted file is a temporary this import owns: the item keeps pointing at the
            // downloaded original, and the temp is deleted when this scope ends.
            return await DeferForCompactionAsync(
                    item,
                    convertTemp.Path is null ? importPath : downloadPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // --- Tag, name, place ---------------------------------------------------------------------
        var placement = await _organizer
            .OrganizeAsync(
                new OrganizeRequest(
                    song,
                    album,
                    credits,
                    library,
                    importPath,
                    extension,
                    media,
                    measuredQuality,
                    item.SourceType,
                    verification.AcoustId,
                    KeepSource: false,

                    // A song owned through a reference file is satisfied by the user's own copy, which
                    // Wondarr does not control: the grab imports into the library and repoints the song,
                    // and nothing recycles the reference file.
                    song.File?.SourceType == SourceTypes.Reference ? null : song.File?.Path),
                cancellationToken)
            .ConfigureAwait(false);

        if (placement.Failure == OrganizeFailure.Tagging)
        {
            // The file passed verification, so a tagging failure is the tag writer's problem, not the
            // peer's: no blocklist entry and no next candidate (every other file would hit the same
            // problem — seen live on 2026-09-29, where a year-only date burned every good candidate).
            // The song stays wanted and the next missing search tries again.
            await FailAsync(
                    item,
                    $"Tagging failed: {placement.Error}",
                    null,
                    null,
                    allowNextAttempt: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return ImportOutcome.Failed;
        }

        if (!placement.Success || placement.FinalPath is null)
        {
            // A placement failure is a configuration problem, not the file's fault: no blocklist entry
            // and no next candidate — the next one would land in the same unwritable folder.
            LogPlacementFailed(_logger, item.Id, placement.Error ?? "the placer gave no final path");

            await FailAsync(
                    item,
                    placement.Error ?? "The file could not be placed in the library.",
                    placement.FinalPath,
                    placement.RecycledPath,
                    allowNextAttempt: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return ImportOutcome.Failed;
        }

        // --- Record -----------------------------------------------------------------------------
        var upgraded = song.File is not null;
        var previousPath = song.File?.Path;
        var file = song.File ?? new SongFile { SongId = song.Id };

        try
        {
            file.Path = placement.FinalPath;
            file.Size = media.SizeBytes;
            file.Codec = media.Codec;
            file.Container = media.Container;
            file.BitrateKbps = media.BitrateKbps;
            file.SampleRate = media.SampleRate;
            file.BitDepth = media.BitDepth;
            file.Channels = media.Channels;
            file.DurationMs = media.DurationMs;
            file.QualityId = measured;
            file.AcoustId = verification.AcoustId;
            file.FingerprintVerified = fingerprintVerified;
            file.SourceType = item.SourceType;
            file.SourceRef = JsonSerializer.Serialize(
                new SourceReference(
                    item.Candidate.Provider,
                    item.Candidate.RemotePath,
                    item.CandidateId,
                    item.Id,
                    item.SearchRunId),
                Json);
            file.ImportedAt = now;
            file.TagsWritten = JsonSerializer.Serialize(placement.TagsWritten, Json);

            if (song.File is null)
            {
                _database.SongFiles.Add(file);
            }

            // Only now is the learned recording id written onto the song, and it is checked once more:
            // the file is going into the library, so it is allowed to teach the song its identity.
            await ApplyRecordingIdAsync(song, learnedMbId, cancellationToken).ConfigureAwait(false);

            _database.History.Add(new HistoryItem
            {
                SongId = song.Id,
                EventType = upgraded ? HistoryEventType.Upgraded : HistoryEventType.Imported,
                SourceInstanceId = item.SourceInstanceId,
                QualityId = measured,
                Data = JsonSerializer.Serialize(
                    new ImportHistoryData(
                        placement.FinalPath,
                        previousPath,
                        placement.RecycledPath,
                        verification.Reason,
                        item.Candidate.Score,
                        verification.AcoustId,
                        verification.FingerprintScore,
                        needsReview ? true : null,
                        learnedMbId),
                    Json),
            });

            await RecordPeerAsync(item, delivered: true, now, cancellationToken).ConfigureAwait(false);

            item.State = QueueItemState.Imported;
            item.StateChangedAt = now;
            item.FinishedAt = now;
            item.Progress = 1;
            item.Message = null;

            // The record step: the file row, the learned MBID, the history and the peer's reputation
            // commit with the item, in one save. Nothing here is committed on its own — a half-written
            // import is worse than a failed one.
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The file already reached the library; only the bookkeeping failed. The item is failed
            // with that said out loud, and the caller gets an outcome, never an exception.
            LogRecordFailed(_logger, item.Id, exception);

            return await FailSafelyAsync(
                    item.Id,
                    $"Imported to {placement.FinalPath} but recording it failed: {exception.Message}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The converted file is in the library and recorded, so the temporary it was made in and
        // the downloaded original are no longer needed. The original goes through the same
        // own-download-folder check every delete does: a file Wondarr did not download into that
        // folder is not Wondarr's to delete.
        if (convertTemp.Path is not null)
        {
            convertTemp.Delete();
            DeleteDownloadedOriginal(item, downloadPath);
        }

        DeleteEmptyDownloadFolder(item, downloadPath);

        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);
        await _events
            .PublishAsync(new SongImportedEvent(song.Id, file.Id, upgraded), cancellationToken)
            .ConfigureAwait(false);

        LogImported(_logger, item.Id, song.Id, placement.FinalPath, measuredName, upgraded);

        return upgraded ? ImportOutcome.Upgraded : ImportOutcome.Imported;
    }

    /// <summary>
    /// Whether the song has a compaction move that has not finished: planned, staged, or failed
    /// with its file still in the staging folder. Such a song's file is not the import's to touch.
    /// </summary>
    private Task<bool> HasUnfinishedCompactionMoveAsync(long songId, CancellationToken cancellationToken) =>
        _database.CompactMoves
            .AsNoTracking()
            .Where(CompactMoveRules.IsUnfinished)
            .AnyAsync(row => row.SongId == songId, cancellationToken);

    /// <summary>
    /// Defers an item while a compaction has its song's file: the item goes back to
    /// <see cref="QueueItemState.Completed"/> with a check a minute out. No attempt is counted,
    /// nothing is blocklisted and no history row is written — the file is exactly where it was.
    /// </summary>
    /// <param name="item">The item being imported.</param>
    /// <param name="importPath">
    /// Where the file to import is right now: the download path, or the transcoded file when the
    /// transcode step already ran. A transcoded file is pointed back at as the item's download
    /// path, so the next pass finds it (and skips the transcode) instead of a deleted original.
    /// </param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    private async Task<ImportOutcome> DeferForCompactionAsync(
        QueueItem item,
        string importPath,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        item.State = QueueItemState.Completed;
        item.StateChangedAt = now;
        item.NextCheckAt = now + CompactionWait;
        item.Message = CompactionWaitMessage;

        if (!string.Equals(item.DownloadPath, importPath, StringComparison.Ordinal))
        {
            item.DownloadPath = importPath;
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);

        LogDeferred(_logger, item.Id, CompactionWaitMessage);

        return ImportOutcome.Deferred;
    }

    /// <summary>Loads the song with everything the import needs.</summary>
    private async Task<Song?> LoadSongAsync(long songId, CancellationToken cancellationToken) =>
        await _database.Songs
            .Include(song => song.AlbumContext)
            .Include(song => song.File)
            .Include(song => song.Artists)
                .ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Whether the grab that produced this item was started by the user.</summary>
    private async Task<bool> IsAutomaticGrabAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var trigger = await _database.SearchRuns
            .AsNoTracking()
            .Where(run => run.Id == item.SearchRunId)
            .Select(run => (SearchTrigger?)run.Trigger)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // A manual grab is the user's decision: whatever it brought replaces what the song had.
        return trigger is not SearchTrigger.Manual;
    }

    /// <summary>
    /// Decides which recording id AcoustID recognised, when the song has none and no other song holds
    /// it: a Deezer-only song learns its MusicBrainz identity from the file that was just verified.
    /// Nothing is written here — the id is adopted in the record step, once the file is really imported.
    /// </summary>
    private async Task<string?> DecideRecordingIdAsync(
        Song song,
        VerificationResult verification,
        CancellationToken cancellationToken)
    {
        var learned = verification.LearnedMbRecordingId;

        if (string.IsNullOrWhiteSpace(learned) || !string.IsNullOrWhiteSpace(song.MbRecordingId))
        {
            return null;
        }

        if (await IsRecordingIdTakenAsync(song.Id, learned, cancellationToken).ConfigureAwait(false))
        {
            LogRecordingIdTaken(_logger, song.Id, learned);

            return null;
        }

        return learned;
    }

    /// <summary>Writes the learned recording id onto the song, checking once more that it is free.</summary>
    private async Task ApplyRecordingIdAsync(Song song, string? learned, CancellationToken cancellationToken)
    {
        if (learned is null)
        {
            return;
        }

        if (await IsRecordingIdTakenAsync(song.Id, learned, cancellationToken).ConfigureAwait(false))
        {
            LogRecordingIdTaken(_logger, song.Id, learned);

            return;
        }

        song.MbRecordingId = learned;
    }

    /// <summary>Whether another song already holds the recording id.</summary>
    private async Task<bool> IsRecordingIdTakenAsync(
        long songId,
        string learned,
        CancellationToken cancellationToken) =>
        await _database.Songs
            .AsNoTracking()
            .AnyAsync(other => other.Id != songId && other.MbRecordingId == learned, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Refuses a file: it is blocklisted for this song, the peer's record drops, the download is
    /// deleted, the item is failed, and the next accepted candidate of the run is grabbed.
    /// </summary>
    /// <param name="item">The queue item the file came in.</param>
    /// <param name="song">The song it was grabbed for.</param>
    /// <param name="reason">Why the file was refused; this is the blocklist reason and the item's message.</param>
    /// <param name="verification">What the verifier said, when it was asked at all.</param>
    /// <param name="qualityId">The measured quality, when it was measured at all.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    private async Task<ImportOutcome> RejectAsync(
        QueueItem item,
        Song song,
        string reason,
        string? verification,
        long? qualityId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var candidate = item.Candidate;

        var data = new RejectionHistoryData(
            reason,
            candidate.DisplayName,
            candidate.Provider,
            candidate.RemotePath,
            verification,
            null);

        var historyItem = new HistoryItem
        {
            SongId = song.Id,
            EventType = HistoryEventType.Rejected,
            SourceInstanceId = item.SourceInstanceId,
            QualityId = qualityId,
            Data = JsonSerializer.Serialize(data, Json),
        };

        _database.History.Add(historyItem);

        // The blocklist row commits with the rest of the refusal: the search reads it before it picks
        // the next candidate, and the candidate that just failed must be skipped.
        _database.Blocklist.Add(new BlocklistItem
        {
            SongId = song.Id,
            SourceType = item.SourceType,
            BlocklistKey = candidate.BlocklistKey,
            Reason = reason,
            ExpiresAt = null,
        });

        await RecordPeerAsync(item, delivered: false, now, cancellationToken).ConfigureAwait(false);

        DeleteDownload(item, item.DownloadPath);

        item.State = QueueItemState.Failed;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Message = reason;

        // The whole refusal in one save: history, blocklist, reputation and the item.
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);

        var next = await TryNextAsync(item, cancellationToken).ConfigureAwait(false);

        // Where the refusal led can only be known after the grab, so the history entry is completed
        // with it — that is the one thing the refusal could not know in advance.
        historyItem.Data = JsonSerializer.Serialize(data with { NextQueueItemId = next }, Json);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRejected(_logger, item.Id, song.Id, reason, next);

        return ImportOutcome.Rejected;
    }

    /// <summary>Fails an item without a blocklist entry, optionally moving on to the next candidate.</summary>
    private async Task FailAsync(
        QueueItem item,
        string message,
        string? finalPath,
        string? recycledPath,
        bool allowNextAttempt,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var data = new FailureHistoryData(message, finalPath, recycledPath, null);

        var historyItem = new HistoryItem
        {
            SongId = item.SongId,
            EventType = HistoryEventType.Failed,
            SourceInstanceId = item.SourceInstanceId,
            Data = JsonSerializer.Serialize(data, Json),
        };

        _database.History.Add(historyItem);

        item.State = QueueItemState.Failed;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Message = message;

        // The item and the history entry of one failure commit together.
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);

        if (!allowNextAttempt)
        {
            return;
        }

        var next = await TryNextAsync(item, cancellationToken).ConfigureAwait(false);

        historyItem.Data = JsonSerializer.Serialize(data with { NextQueueItemId = next }, Json);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an item failed after something went wrong, and never throws: whoever called
    /// <see cref="ImportAsync"/> gets an outcome, not an exception. Everything the failed attempt left
    /// in the change tracker is dropped first, so the item is written back exactly as it is stored.
    /// </summary>
    /// <param name="queueItemId">The item to fail.</param>
    /// <param name="message">Why, as the user will read it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Always <see cref="ImportOutcome.Failed"/>.</returns>
    private async Task<ImportOutcome> FailSafelyAsync(
        long queueItemId,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            _database.ChangeTracker.Clear();

            var item = await _database.QueueItems
                .FirstOrDefaultAsync(entry => entry.Id == queueItemId, cancellationToken)
                .ConfigureAwait(false);

            if (item is null)
            {
                return ImportOutcome.Failed;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            item.State = QueueItemState.Failed;
            item.StateChangedAt = now;
            item.FinishedAt = now;
            item.Message = message;

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Nothing more can be written down. The caller still hears "failed" rather than an
            // exception it has no answer for.
            LogFailureNotRecorded(_logger, queueItemId, exception);
        }

        return ImportOutcome.Failed;
    }

    /// <summary>
    /// Asks the search for the next accepted candidate of the same run, within the attempt budget.
    /// A grab that fails is logged and reported as "no more candidates": a refused file must never
    /// take the import down with it.
    /// </summary>
    private async Task<string> TryNextAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _options.CurrentValue.MaxAutoAttemptsPerSearch)
        {
            return NoMoreCandidates;
        }

        try
        {
            var next = await _search
                .GrabBestAsync(item.SearchRunId, item.Attempt + 1, cancellationToken)
                .ConfigureAwait(false);

            return next?.ToString(CultureInfo.InvariantCulture) ?? NoMoreCandidates;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogNextAttemptFailed(_logger, item.Id, exception);

            return NoMoreCandidates;
        }
    }

    /// <summary>The song's stored version flags as a mask; unknown names are ignored.</summary>
    private static VersionFlags ParseFlags(IEnumerable<string> names)
    {
        var flags = VersionFlags.None;

        foreach (var name in names)
        {
            if (VersionFlagNames.TryParse(name, out var single))
            {
                flags |= single;
            }
        }

        return flags;
    }

    /// <summary>Registers the peer's delivery for Soulseek; other sources have no peer reputation yet.</summary>
    /// <param name="item">The queue item whose candidate came from the peer.</param>
    /// <param name="delivered">Whether the peer delivered (success) or failed us.</param>
    /// <param name="now">The instant the delivery is recorded at.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <remarks>
    /// Nothing is saved here: the import owns the save, and one outcome — the file row, the history
    /// entry, the reputation and the item — commits as one. The arithmetic is the same as
    /// <c>SoulseekUserService.RecordSuccessAsync</c>/<c>RecordFailureAsync</c>; those few lines are
    /// duplicated on purpose, because that service saves on its own and this one must not.
    /// </remarks>
    private async Task RecordPeerAsync(
        QueueItem item,
        bool delivered,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (item.SourceType != SourceTypes.Soulseek || item.Candidate.Provider is not { Length: > 0 } provider)
        {
            return;
        }

        var user = await _database.SoulseekUsers
            .FirstOrDefaultAsync(candidate => candidate.Username == provider, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            user = new SoulseekUser { Username = provider };
            _database.SoulseekUsers.Add(user);
        }

        if (delivered)
        {
            user.Successes++;
            user.LastSuccessAt = now;

            return;
        }

        user.Failures++;
        user.RecentFailures.Add(now);

        // Only the newest ten are kept: that is all the 24-hour rule needs, and the list is a JSON column.
        if (user.RecentFailures.Count > MaxRecentFailures)
        {
            user.RecentFailures.RemoveRange(0, user.RecentFailures.Count - MaxRecentFailures);
        }
    }

    /// <summary>Publishes the item's new state.</summary>
    private Task PublishQueueItemAsync(QueueItem item, CancellationToken cancellationToken) =>
        _events.PublishAsync(new QueueItemChangedEvent(item.Id, item.SongId, item.State), cancellationToken);

    /// <summary>
    /// Deletes the download and the per-grab folder it sits in, when that folder is left empty. A path
    /// that is not the item's own download folder is refused with a line in the log and nothing else:
    /// a file Wondarr did not download into that folder is not Wondarr's to delete.
    /// </summary>
    private void DeleteDownload(QueueItem item, string? downloadPath)
    {
        if (!IsOwnDownloadFolder(item, downloadPath, out var folder))
        {
            LogDeleteRefused(_logger, item.Id, downloadPath);

            return;
        }

        try
        {
            var path = Path.GetFullPath(downloadPath!);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            RemoveFolderWhenEmpty(folder);
        }
        catch (IOException exception)
        {
            // A download that will not delete is worth a line, not a failed import: the rejection
            // itself has already been recorded.
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
    }

    /// <summary>
    /// Removes the item's own download folder — the one the placed file moved out of — and only while
    /// it is empty.
    /// </summary>
    private void DeleteEmptyDownloadFolder(QueueItem item, string? downloadPath)
    {
        if (!IsOwnDownloadFolder(item, downloadPath, out var folder))
        {
            LogDeleteRefused(_logger, item.Id, downloadPath);

            return;
        }

        try
        {
            RemoveFolderWhenEmpty(folder);
        }
        catch (IOException exception)
        {
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
    }

    /// <summary>
    /// Deletes the downloaded original a converted file was made from, once that file is placed and
    /// recorded — the file only, never the folder, which <see cref="DeleteEmptyDownloadFolder"/>
    /// cleans up right after. The same own-download-folder check <see cref="DeleteDownload"/> uses
    /// applies: a file Wondarr did not download into that folder is not Wondarr's to delete.
    /// </summary>
    private void DeleteDownloadedOriginal(QueueItem item, string? downloadPath)
    {
        if (!IsOwnDownloadFolder(item, downloadPath, out _))
        {
            LogDeleteRefused(_logger, item.Id, downloadPath);

            return;
        }

        try
        {
            var path = Path.GetFullPath(downloadPath!);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogDeleteFailed(_logger, downloadPath!, exception.Message);
        }
    }

    /// <summary>
    /// The temporary file a non-YouTube conversion is written to. It is deleted again on every
    /// exit but the one that places it — the organizer moves it into the library, so a temp that is
    /// still there when the scope ends was never placed, and a <c>.wondarr-convert.</c> file is
    /// never left behind.
    /// </summary>
    private sealed class ConvertTempFile : IDisposable
    {
        private string? _path;

        /// <summary>Gets or sets the temp file this scope owns, or <see langword="null"/> while there is none.</summary>
        public string? Path
        {
            get => _path;
            set => _path = value;
        }

        /// <summary>Deletes the temp file, when it is still there.</summary>
        public void Delete()
        {
            if (_path is not { } path || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temp that will not delete is worth nothing: the import's outcome does not
                // depend on it, and the next attempt deletes it before it writes its own.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <inheritdoc />
        public void Dispose() => Delete();
    }

    /// <summary>
    /// Resolves a download into the item's own per-grab folder, or refuses. It is the item's own folder
    /// only when its full path <em>ends</em> with the item's <c>Destination</c> segments — compared
    /// segment by segment, <c>/</c> and <c>\</c> alike — and the file sits directly inside it. A path
    /// with a <c>..</c> segment, or one whose file or folder chain (up to the destination root) is a
    /// reparse point, is refused too: deleting through a symlink would leave the real file behind, or
    /// take a folder somewhere else entirely with it.
    /// </summary>
    /// <param name="item">The queue item that owns the download.</param>
    /// <param name="downloadPath">The path the source wrote to.</param>
    /// <param name="folder">The folder that may be removed when it is empty.</param>
    /// <returns>Whether the download is Wondarr's to delete.</returns>
    private static bool IsOwnDownloadFolder(QueueItem item, string? downloadPath, out string folder)
    {
        folder = string.Empty;

        if (string.IsNullOrWhiteSpace(downloadPath))
        {
            return false;
        }

        // ".." is only visible before normalisation: Path.GetFullPath would resolve it away and hide it.
        var given = SplitSegments(downloadPath);

        if (given.Count == 0 || given.Contains(".."))
        {
            return false;
        }

        var wanted = SplitSegments(item.Destination);

        if (wanted.Count == 0)
        {
            return false;
        }

        var path = Path.GetFullPath(downloadPath);
        var directory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var have = SplitSegments(directory);

        if (have.Count < wanted.Count)
        {
            return false;
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            if (!string.Equals(have[have.Count - wanted.Count + index], wanted[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (File.Exists(path) && IsReparsePoint(path))
        {
            return false;
        }

        var current = directory;

        for (var index = 0; index < wanted.Count; index++)
        {
            if (IsReparsePoint(current))
            {
                return false;
            }

            current = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(current))
            {
                break;
            }
        }

        folder = directory;

        return true;
    }

    /// <summary>Splits a path or a destination into its segments, treating <c>/</c> and <c>\</c> alike.</summary>
    private static List<string> SplitSegments(string path) =>
        [.. path.Split(Separators, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Whether a path is a symlink, a junction or another reparse point; unreadable counts as one.</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // Anything that cannot be looked at is left alone.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Deletes one folder, and only while it holds nothing at all; never a parent, never recursively.</summary>
    private static void RemoveFolderWhenEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {SongId} from queue item {QueueItemId} to {Path} ({Quality}{Upgraded})")]
    private static partial void LogImported(
        ILogger logger,
        long queueItemId,
        long songId,
        string path,
        string quality,
        bool upgraded);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected queue item {QueueItemId} of song {SongId}: {Reason} (next: {Next})")]
    private static partial void LogRejected(ILogger logger, long queueItemId, long songId, string reason, string next);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deferred queue item {QueueItemId}: {Reason}")]
    private static partial void LogDeferred(ILogger logger, long queueItemId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue item {QueueItemId} has no downloaded file at {Path}")]
    private static partial void LogDownloadMissing(ILogger logger, long queueItemId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue item {QueueItemId} could not be placed: {Reason}")]
    private static partial void LogPlacementFailed(ILogger logger, long queueItemId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue item {QueueItemId} cannot be imported: the download has a .{Extension} extension")]
    private static partial void LogExtensionRefused(ILogger logger, long queueItemId, string extension);

    [LoggerMessage(Level = LogLevel.Error, Message = "The transcode of queue item {QueueItemId} failed")]
    private static partial void LogTranscodeFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refusing to delete {Path}: it is not the own download folder of queue item {QueueItemId}")]
    private static partial void LogDeleteRefused(ILogger logger, long queueItemId, string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The import of queue item {QueueItemId} could not be recorded")]
    private static partial void LogRecordFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queue item {QueueItemId} could not even be marked failed")]
    private static partial void LogFailureNotRecorded(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Song {SongId} already has a file with the recording id {RecordingId}; the learned id was not taken")]
    private static partial void LogRecordingIdTaken(ILogger logger, long songId, string recordingId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The next candidate for queue item {QueueItemId} could not be grabbed")]
    private static partial void LogNextAttemptFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the download {Path}: {Reason}")]
    private static partial void LogDeleteFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue item {QueueItemId} is fake lossless: the spectrum stops at {CutoffHz} Hz")]
    private static partial void LogFakeLossless(ILogger logger, long queueItemId, double cutoffHz);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Spectral check of queue item {QueueItemId}: {Outcome} (cutoff {CutoffHz} Hz)")]
    private static partial void LogSpectralVerdict(ILogger logger, long queueItemId, SpectralOutcome outcome, double? cutoffHz);

    [LoggerMessage(Level = LogLevel.Error, Message = "The import of queue item {QueueItemId} failed")]
    private static partial void LogImportFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No queue item has the id {QueueItemId}")]
    private static partial void LogMissingItem(ILogger logger, long queueItemId);

    /// <summary>Where a file came from, as <c>song_file.source_ref</c> stores it.</summary>
    private sealed record SourceReference(
        string? Provider,
        string RemotePath,
        long CandidateId,
        long QueueItemId,
        long SearchRunId);

    /// <summary>The history payload of an import.</summary>
    private sealed record ImportHistoryData(
        string Path,
        string? PreviousPath,
        string? RecycledPath,
        string Verification,
        int Score,
        string? AcoustId,
        double? FingerprintScore,
        bool? NeedsReview,
        string? LearnedMbid);

    /// <summary>The history payload of a rejection.</summary>
    private sealed record RejectionHistoryData(
        string Reason,
        string? Candidate,
        string? Provider,
        string? RemotePath,
        string? Verification,
        string? NextQueueItemId);

    /// <summary>The history payload of a failure that is not a rejection.</summary>
    private sealed record FailureHistoryData(
        string Error,
        string? FinalPath,
        string? RecycledPath,
        string? NextQueueItemId);
}
