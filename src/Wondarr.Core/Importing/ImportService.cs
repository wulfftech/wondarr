using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Messaging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
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
    private readonly ITagWriter _tagWriter;
    private readonly IFilePlacer _placer;
    private readonly ICoverFetcher _coverFetcher;
    private readonly ISongSearchService _search;
    private readonly IEventAggregator _events;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportService> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportService"/> class.</summary>
    /// <param name="database">The Wondarr database; the item, the song and the file are written through it.</param>
    /// <param name="verifier">Decides whether the file is the wanted recording.</param>
    /// <param name="tagWriter">Writes the tag set into the file.</param>
    /// <param name="placer">Puts the file at its library path, recycling what it replaces.</param>
    /// <param name="coverFetcher">Downloads the cover to embed.</param>
    /// <param name="search">Grabs the next candidate when a file is refused.</param>
    /// <param name="events">Publishes the queue and import events.</param>
    /// <param name="options">The attempt budget a rejected file works within.</param>
    /// <param name="timeProvider">The clock every timestamp comes from.</param>
    /// <param name="logger">The logger.</param>
    public ImportService(
        WondarrDbContext database,
        IDownloadVerifier verifier,
        ITagWriter tagWriter,
        IFilePlacer placer,
        ICoverFetcher coverFetcher,
        ISongSearchService search,
        IEventAggregator events,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<ImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(tagWriter);
        ArgumentNullException.ThrowIfNull(placer);
        ArgumentNullException.ThrowIfNull(coverFetcher);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _verifier = verifier;
        _tagWriter = tagWriter;
        _placer = placer;
        _coverFetcher = coverFetcher;
        _search = search;
        _events = events;
        _options = options;
        _timeProvider = timeProvider;
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

        // --- Verify -----------------------------------------------------------------------------
        var credits = song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => (credit.Artist, credit.Role))
            .ToList();

        var verification = await _verifier
            .VerifyAsync(
                new VerificationRequest(
                    downloadPath,
                    song.MbRecordingId,
                    song.Title,
                    [.. credits.Where(credit => credit.Role == ArtistRole.Main).Select(credit => credit.Artist.Name)],
                    song.DurationMs,
                    ParseFlags(song.VersionFlags),
                    profile.DurationToleranceMs),
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

        // --- Tag --------------------------------------------------------------------------------
        var cover = await _coverFetcher.FetchAsync(album.CoverUrl, cancellationToken).ConfigureAwait(false);

        var tags = TagSetBuilder.Build(song, album, credits, verification, cover);
        var tagResult = await _tagWriter.WriteAsync(downloadPath, tags, cancellationToken).ConfigureAwait(false);

        if (!tagResult.Success)
        {
            return await RejectAsync(
                    item,
                    song,
                    $"Tagging failed: {tagResult.Error}",
                    verification.Reason,
                    measured,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // --- Name -------------------------------------------------------------------------------
        var template = string.IsNullOrWhiteSpace(library.NamingTemplate)
            ? NamingTemplate.PresetTemplates[library.Layout]
            : library.NamingTemplate;

        var primaryArtist = PrimaryArtist(song, credits);

        var relative = NamingTemplate.Render(
            template,
            NamingValuesBuilder.Build(song, album, primaryArtist, media, measuredQuality, item.SourceType),
            new NamingOptions(Extension: extension));

        // --- Place ------------------------------------------------------------------------------
        var placement = await _placer
            .PlaceAsync(
                new PlacementRequest(
                    downloadPath,
                    library.RootPath,
                    relative,
                    extension,
                    TransferMode.Move,
                    song.File?.Path),
                cancellationToken)
            .ConfigureAwait(false);

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
            file.TagsWritten = JsonSerializer.Serialize(tagResult.Written, Json);

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

        DeleteEmptyDownloadFolder(item, downloadPath);

        await PublishQueueItemAsync(item, cancellationToken).ConfigureAwait(false);
        await _events
            .PublishAsync(new SongImportedEvent(song.Id, file.Id, upgraded), cancellationToken)
            .ConfigureAwait(false);

        LogImported(_logger, item.Id, song.Id, placement.FinalPath, measuredName, upgraded);

        return upgraded ? ImportOutcome.Upgraded : ImportOutcome.Imported;
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

    /// <summary>The song's primary artist: the one it points at, or the first credit there is.</summary>
    private static Artist PrimaryArtist(Song song, List<(Artist Artist, ArtistRole Role)> credits)
    {
        foreach (var credit in credits)
        {
            if (credit.Artist.Id == song.PrimaryArtistId)
            {
                return credit.Artist;
            }
        }

        foreach (var credit in credits)
        {
            if (credit.Role == ArtistRole.Main)
            {
                return credit.Artist;
            }
        }

        return credits.Count > 0
            ? credits[0].Artist
            : throw new InvalidOperationException(
                string.Concat(
                    "Song ",
                    song.Id.ToString(CultureInfo.InvariantCulture),
                    " has no credited artist."));
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