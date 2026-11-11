using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Decisions;

/// <summary>
/// Once per database, teaches every song what its refusals recorded before 0.1.18 already said
/// (MATCHING_ENGINE.md §6.2): a title the fingerprint check named in the song's place in two or more
/// refusals becomes a learned other title, and each refusal that proved a file passing on a compilation path
/// on its title alone to be another recording (the fingerprint, the file's own artist tag, or the user's
/// "Remove file") counts one compilation miss. Only what the import would have written is written, as blocklist
/// rows with no expiry, one save per song.
/// </summary>
/// <remarks>
/// It never blocks startup (it yields first, like the library root adoption) and records
/// <see cref="SettingKey"/> once the pass is through, so a later start does not repeat it. A song that fails is
/// logged and skipped; cancellation stops the pass and leaves the setting unset, and the pass is idempotent.
/// </remarks>
public sealed partial class LearnedEvidenceBackfill : BackgroundService
{
    /// <summary>The setting that holds the version of the backfill this database has been through.</summary>
    public const string SettingKey = "learned.backfill_version";

    /// <summary>The version of the backfill this build runs; a database below it is run once.</summary>
    public const int CurrentVersion = 1;

    private const string FingerprintReasonOpening = "Fingerprint belongs to a different recording:";
    private const string ArtistReasonMiddle = ", and the ";
    private const string ArtistReasonEnd = " names another artist (";

    private static readonly Regex FingerprintReason = new(
        @"^Fingerprint belongs to a different recording: (?<title>.+) \((?<id>[0-9a-fA-F-]{36})\), score (?<score>[0-9.]+)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LearnedEvidenceBackfill> _logger;

    /// <summary>Initialises a new instance of the <see cref="LearnedEvidenceBackfill"/> class.</summary>
    /// <param name="scopeFactory">Gives the pass a scope of its own.</param>
    /// <param name="logger">The log sink.</param>
    public LearnedEvidenceBackfill(IServiceScopeFactory scopeFactory, ILogger<LearnedEvidenceBackfill> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs the pass when this database has not been through it: every song with a refusal or removal that
    /// taught something, then the setting. Does nothing when the setting is already current.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between and inside songs.</param>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();

        if (await settings.GetAsync<int>(SettingKey, cancellationToken).ConfigureAwait(false) >= CurrentVersion)
        {
            return;
        }

        var acceptScore = (scope.ServiceProvider.GetService<IOptionsMonitor<AcoustIdOptions>>()?.CurrentValue
            ?? new AcoustIdOptions()).AcceptScore;

        // One scan of the blocklist for the songs that have such a row at all; everything else is per song.
        var songIds = await database.Blocklist
            .AsNoTracking()
            .Where(entry => entry.SongId != null
                && (entry.Reason.StartsWith(FingerprintReasonOpening)
                    || entry.Reason == SongFileRemover.Reason
                    || (entry.Reason.Contains(ArtistReasonMiddle) && entry.Reason.Contains(ArtistReasonEnd))))
            .Select(entry => entry.SongId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var learnedTitles = 0;
        var countedMisses = 0;

        foreach (var songId in songIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (titles, misses) = await BackfillSongAsync(database, songId, acceptScore, cancellationToken).ConfigureAwait(false);
                learnedTitles += titles;
                countedMisses += misses;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSongFailed(_logger, songId, exception);
            }
            finally
            {
                database.ChangeTracker.Clear();
            }
        }

        await settings.SetAsync(SettingKey, CurrentVersion, cancellationToken).ConfigureAwait(false);

        LogDone(_logger, songIds.Count, learnedTitles, countedMisses);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yields first: the pass reads the whole blocklist and must never hold up the host's start.
        await Task.Yield();

        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A catch-up must never stop the host; the setting stays unset and the next start repeats it.
            LogFailed(_logger, exception);
        }
    }

    private static async Task<(int Titles, int Misses)> BackfillSongAsync(
        WondarrDbContext database,
        long songId,
        double acceptScore,
        CancellationToken cancellationToken)
    {
        // The song's own rows: what it has learned, and the refusals and removals that taught something.
        var rows = await database.Blocklist
            .AsNoTracking()
            .Where(entry => entry.SongId == songId)
            .Select(entry => new Row(entry.SourceType, entry.BlocklistKey, entry.Reason, entry.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keys = rows.Select(row => row.Key).ToList();
        var learnedAlready = BlocklistKeys.OtherTitles(keys)
            .Select(title => TextMatching.Normalize(title))
            .ToHashSet(StringComparer.Ordinal);
        var missesKnown = rows
            .Where(row => row.Key.StartsWith(BlocklistKeys.LearnedOpening, StringComparison.Ordinal))
            .Select(row => row.Key)
            .ToHashSet(StringComparer.Ordinal);

        var fingerprint = new List<(Row Row, string Title)>();
        var other = new List<Row>();

        foreach (var row in rows)
        {
            if (row.Key.StartsWith(BlocklistKeys.LearnedOpening, StringComparison.Ordinal))
            {
                continue;
            }

            if (FingerprintReason.Match(row.Reason) is { Success: true } match
                && double.TryParse(match.Groups["score"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                && score >= acceptScore)
            {
                fingerprint.Add((row, match.Groups["title"].Value));
            }
            else if (row.Reason == SongFileRemover.Reason
                || (row.Reason.Contains(ArtistReasonMiddle, StringComparison.Ordinal)
                    && row.Reason.Contains(ArtistReasonEnd, StringComparison.Ordinal)))
            {
                other.Add(row);
            }
        }

        if (fingerprint.Count == 0 && other.Count == 0)
        {
            return (0, 0);
        }

        var song = await database.Songs
            .Include(entry => entry.Artists)
                .ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(entry => entry.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return (0, 0);
        }

        var added = 0;
        var titles = 0;

        // Other titles: one refusal writes its rows in one save, so rows sharing a CreatedAt are one refusal.
        foreach (var group in fingerprint.GroupBy(entry => TextMatching.Normalize(entry.Title)))
        {
            var refusals = group.Select(entry => entry.Row.CreatedAt).Distinct().Count();
            var newest = group.MaxBy(entry => entry.Row.CreatedAt);

            if (refusals >= 2
                && group.Key.Length > 0
                && LearnedEvidence.TitleToLearn(newest.Title, song.Title) is { } title
                && learnedAlready.Add(TextMatching.Normalize(title)))
            {
                database.Blocklist.Add(NewRow(song.Id, newest.Row, BlocklistKeys.OtherTitle(title)));
                titles++;
                added++;
            }
        }

        // Compilation misses: every refusal that proved the file was not the song, by the candidate it refused.
        var proofs = fingerprint.Select(entry => entry.Row).Concat(other)
            .GroupBy(row => row.Key)
            .Select(group => group.MaxBy(row => row.CreatedAt)!)
            .ToList();

        var misses = 0;

        if (proofs.Count > 0)
        {
            var records = await NewestSnapshotsAsync(database, song.Id, proofs.Select(row => row.Key).ToList(), cancellationToken)
                .ConfigureAwait(false);

            foreach (var proof in proofs)
            {
                var missKey = BlocklistKeys.CompilationMiss(proof.Key);

                if (records.TryGetValue(proof.Key, out var record)
                    && !missesKnown.Contains(missKey)
                    && LearnedMisses.PassedOnTitleAlone(song, record))
                {
                    database.Blocklist.Add(NewRow(song.Id, proof, missKey));
                    missesKnown.Add(missKey);
                    misses++;
                    added++;
                }
            }
        }

        if (added > 0)
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (titles, misses);
    }

    /// <summary>
    /// For each key, the newest stored candidate of the song with it whose snapshot reads back. Two queries
    /// for the song; one more per key whose newest row has an unreadable snapshot.
    /// </summary>
    private static async Task<Dictionary<string, CandidateRecord>> NewestSnapshotsAsync(
        WondarrDbContext database,
        long songId,
        List<string> keys,
        CancellationToken cancellationToken)
    {
        var newestIds = await database.Candidates
            .AsNoTracking()
            .Where(candidate => candidate.SongId == songId && keys.Contains(candidate.BlocklistKey))
            .GroupBy(candidate => candidate.BlocklistKey)
            .Select(group => group.Max(candidate => candidate.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = new Dictionary<string, CandidateRecord>(StringComparer.Ordinal);

        if (newestIds.Count == 0)
        {
            return found;
        }

        var newest = await database.Candidates
            .AsNoTracking()
            .Where(candidate => newestIds.Contains(candidate.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in newest)
        {
            var current = record;

            while (current is not null && !LearnedMisses.HasSnapshot(current))
            {
                var below = current.Id;
                current = await database.Candidates
                    .AsNoTracking()
                    .Where(candidate => candidate.SongId == songId && candidate.BlocklistKey == record.BlocklistKey && candidate.Id < below)
                    .OrderByDescending(candidate => candidate.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (current is not null)
            {
                found[record.BlocklistKey] = current;
            }
        }

        return found;
    }

    private static BlocklistItem NewRow(long songId, Row source, string key) => new()
    {
        SongId = songId,
        SourceType = source.SourceType,
        BlocklistKey = key,
        Reason = source.Reason,
        ExpiresAt = null,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Learned evidence backfill: read {Songs} songs, learned {Titles} other titles, counted {Misses} compilation misses")]
    private static partial void LogDone(ILogger logger, int songs, int titles, int misses);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Learned evidence backfill could not read song {SongId}; going on with the next")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not backfill the learned evidence at startup")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    /// <summary>The columns of a blocklist row the pass reads.</summary>
    private sealed record Row(string SourceType, string Key, string Reason, DateTime CreatedAt);
}
