using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.Media;

/// <summary>How <see cref="IReplayGainApplier.ApplyAsync"/> left one file.</summary>
public enum ReplayGainOutcome
{
    /// <summary>The file was measured, its values stored and its tags written.</summary>
    Done,

    /// <summary>The file was not touched (a reference file, one that already has values, or one that is busy).</summary>
    Skipped,

    /// <summary>The file could not be measured or tagged; it is exactly as it was.</summary>
    Failed,
}

/// <summary>Measures one file and writes its ReplayGain tags in place.</summary>
public interface IReplayGainApplier
{
    /// <summary>Measures, stores and tags one song file under the song's lock.</summary>
    /// <param name="songFileId">The <c>song_file</c> row.</param>
    /// <param name="libraryId">The library the run is for; the file is skipped if its song has moved elsewhere. <see langword="null"/> for any.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome and, for a failure or a skip, why.</returns>
    Task<(ReplayGainOutcome Outcome, string? Reason)> ApplyAsync(
        long songFileId,
        long? libraryId,
        CancellationToken cancellationToken);
}

/// <summary>The one implementation of <see cref="IReplayGainApplier"/>.</summary>
public sealed partial class ReplayGainApplier : IReplayGainApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WondarrDbContext _database;
    private readonly IReplayGainAnalyzer _analyzer;
    private readonly ITagWriter _tagWriter;
    private readonly ISongFileLock _songFileLock;
    private readonly ILogger<ReplayGainApplier> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReplayGainApplier"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="analyzer">Measures the loudness.</param>
    /// <param name="tagWriter">Writes the two tags through its temporary-copy-and-replace path.</param>
    /// <param name="songFileLock">The per-song lock imports, compaction and conversion take.</param>
    /// <param name="logger">The logger.</param>
    public ReplayGainApplier(
        WondarrDbContext database,
        IReplayGainAnalyzer analyzer,
        ITagWriter tagWriter,
        ISongFileLock songFileLock,
        ILogger<ReplayGainApplier> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(tagWriter);
        ArgumentNullException.ThrowIfNull(songFileLock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _analyzer = analyzer;
        _tagWriter = tagWriter;
        _songFileLock = songFileLock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(ReplayGainOutcome Outcome, string? Reason)> ApplyAsync(
        long songFileId,
        long? libraryId,
        CancellationToken cancellationToken)
    {
        var songId = await _database.SongFiles
            .AsNoTracking()
            .Where(candidate => candidate.Id == songFileId)
            .Select(candidate => (long?)candidate.SongId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (songId is null)
        {
            return (ReplayGainOutcome.Skipped, "The file no longer exists.");
        }

        await using var songFileLock = await _songFileLock.AcquireAsync(songId.Value, cancellationToken).ConfigureAwait(false);

        // Read after the lock is held: an import or a conversion may have replaced the row while this
        // run waited for it.
        var file = await _database.SongFiles
            .FirstOrDefaultAsync(candidate => candidate.Id == songFileId, cancellationToken)
            .ConfigureAwait(false);

        if (file is null)
        {
            return (ReplayGainOutcome.Skipped, "The file no longer exists.");
        }

        // The library may have been switched off, or the song moved, since the run listed its files.
        var current = await _database.Songs
            .AsNoTracking()
            .Where(candidate => candidate.Id == file.SongId)
            .Join(
                _database.Libraries,
                candidate => candidate.LibraryId,
                library => library.Id,
                (candidate, library) => new { candidate.LibraryId, library.ReplayGain })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current is null || !current.ReplayGain || (libraryId is { } expected && current.LibraryId != expected))
        {
            return (ReplayGainOutcome.Skipped, "The song's library no longer has ReplayGain on, or the song moved.");
        }

        if (string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal))
        {
            return (ReplayGainOutcome.Skipped, "A reference file is the user's own and is never re-tagged.");
        }

        if (file.ReplayGainDb is not null && file.ReplayGainPeak is not null)
        {
            return (ReplayGainOutcome.Skipped, "The file already has ReplayGain values.");
        }

        if (await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == file.SongId, cancellationToken)
                .ConfigureAwait(false)
            || await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(item => item.SongId == file.SongId && item.State == QueueItemState.Importing, cancellationToken)
                .ConfigureAwait(false))
        {
            return (ReplayGainOutcome.Skipped, "The song's file is being moved or imported; try again later.");
        }

        if (!File.Exists(file.Path))
        {
            return (ReplayGainOutcome.Failed, "The file is not on disk.");
        }

        var values = await _analyzer.MeasureAsync(file.Path, cancellationToken).ConfigureAwait(false);

        if (values is null)
        {
            return (ReplayGainOutcome.Failed, "The loudness could not be measured.");
        }

        var written = await _tagWriter
            .WriteReplayGainAsync(file.Path, values.GainDb, values.Peak, cancellationToken)
            .ConfigureAwait(false);

        if (!written.Success)
        {
            return (ReplayGainOutcome.Failed, written.Error ?? "The tags could not be written.");
        }

        file.ReplayGainDb = values.GainDb;
        file.ReplayGainPeak = values.Peak;
        file.Size = new FileInfo(file.Path).Length;
        file.TagsWritten = MergeWritten(file.TagsWritten, written.Written);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogApplied(_logger, songId.Value, values.GainDb);

        return (ReplayGainOutcome.Done, null);
    }

    /// <summary>The file row's snapshot of the written tags, with the two new fields added.</summary>
    private static string MergeWritten(string? existing, IReadOnlyDictionary<string, string> added)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(existing))
        {
            try
            {
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(existing, Json) ?? [])
                {
                    merged[pair.Key] = pair.Value;
                }
            }
            catch (JsonException)
            {
                // A snapshot that no longer parses is replaced by the fields known now.
            }
        }

        foreach (var pair in added)
        {
            merged[pair.Key] = pair.Value;
        }

        return JsonSerializer.Serialize(merged, Json);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ReplayGain applied to song {SongId}: {GainDb} dB")]
    private static partial void LogApplied(ILogger logger, long songId, double gainDb);
}
