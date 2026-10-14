using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Importing;

/// <summary>
/// The restart check for an item a previous run left in <see cref="QueueItemState.Importing"/>: did
/// its file already land in the library? An import that was stopped between the move and the record
/// leaves the file at its new path and the song row unchanged; resetting such an item would grab the
/// next candidate and file it beside an untracked copy (code review 2026-10-10, R-012 and owner
/// question 18).
/// </summary>
/// <remarks>
/// Deliberately conservative. The destination is rebuilt from the library's naming template, and the
/// naming tokens that depend on what the file measured (bitrate, bit depth) cannot be known after the
/// download has gone, so a template that uses them is simply not found and the item is reset as it
/// always was. The same goes for a destination that is not exactly one new file: when unsure, reset
/// and log.
/// </remarks>
internal static partial class ImportRecovery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Finds the file a stopped import placed, or <see langword="null"/> when there is no evidence of one.
    /// </summary>
    /// <param name="database">The database; the item's song, library and candidate are read through it.</param>
    /// <param name="item">The item that was importing.</param>
    /// <param name="logger">Says why a search ended without a file.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The path of the one new file at the song's expected destination, or <see langword="null"/>.</returns>
    internal static async Task<string?> FindLandedFileAsync(
        WondarrDbContext database,
        QueueItem item,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The download is still there: nothing was moved, and the item is simply tried again.
        if (string.IsNullOrWhiteSpace(item.DownloadPath) || File.Exists(item.DownloadPath))
        {
            return null;
        }

        var song = await database.Songs
            .Include(candidate => candidate.AlbumContext)
            .Include(candidate => candidate.File)
            .Include(candidate => candidate.Artists).ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(candidate => candidate.Id == item.SongId, cancellationToken)
            .ConfigureAwait(false);

        var library = song is null
            ? null
            : await database.Libraries
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == song.LibraryId, cancellationToken)
                .ConfigureAwait(false);

        var quality = await database.Qualities
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == item.Candidate.QualityId, cancellationToken)
            .ConfigureAwait(false);

        // A reference file is the user's own and is never touched, recovery included.
        if (song?.File is { SourceType: SourceTypes.Reference })
        {
            NoDestination(logger, item.Id);

            return null;
        }

        if (song?.AlbumContext is not { } album || library is null || quality is null)
        {
            NoDestination(logger, item.Id);

            return null;
        }

        var credits = song.OrderedCredits();

        var downloadExtension = Path.GetExtension(item.DownloadPath).TrimStart('.').ToLowerInvariant();
        var extensions = new HashSet<string>(StringComparer.Ordinal);

        if (downloadExtension.Length > 0)
        {
            extensions.Add(downloadExtension);
        }

        // A conversion changes the extension the file lands under.
        var policy = LibraryOutputPolicy.Parse(library.OutputPolicy);
        var rule = policy.RuleFor(item.SourceType, quality.Lossless);

        if (rule.Codec != OutputCodec.Keep)
        {
            extensions.Add(rule.Container);
        }

        var matches = new List<string>();
        var since = DateTime.SpecifyKind(item.StateChangedAt, DateTimeKind.Utc);

        foreach (var extension in extensions)
        {
            string relative;

            try
            {
                var media = new MediaInfo(extension, extension, null, null, null, null, 0, quality.Lossless, 0);

                relative = LibraryOrganizer.RelativePathFor(
                    song,
                    album,
                    LibraryOrganizer.PrimaryArtist(song, credits),
                    media,
                    quality,
                    item.SourceType,
                    library,
                    extension);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A template this cannot render without the measured file: unsure, so reset.
                NoDestination(logger, item.Id);

                return null;
            }

            var target = Path.GetFullPath(
                Path.Combine(library.RootPath, relative.Replace('/', Path.DirectorySeparatorChar) + "." + extension));

            // Inside the library only: the same rule the placer holds itself to.
            if (!PathRules.IsStrictlyInside(library.RootPath, target))
            {
                continue;
            }

            // The placer's own bound: it numbers a clash (2), (3) … up to the same limit.
            for (var suffix = 1; suffix <= FilePlacer.MaxCollisionSuffix; suffix++)
            {
                var candidate = suffix == 1
                    ? target
                    : Path.Combine(
                        Path.GetDirectoryName(target) ?? string.Empty,
                        $"{Path.GetFileNameWithoutExtension(target)} ({suffix}){Path.GetExtension(target)}");

                // A file the song row already points at is the song's own; one written before the
                // import began is somebody else's. Only a file this import wrote counts.
                if (File.Exists(candidate)
                    && !(song.File is { } held && PathRules.AreEqual(held.Path, candidate))
                    && File.GetLastWriteTimeUtc(candidate) >= since)
                {
                    // A file another song already owns is that song's, however new it is.
                    if (await IsOwnedByAnotherSongAsync(database, song.Id, candidate, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    matches.Add(candidate);
                }
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            AmbiguousDestination(logger, item.Id, matches.Count);
        }

        return null;
    }

    /// <summary>Whether some other song's file row already points at <paramref name="path"/>.</summary>
    private static async Task<bool> IsOwnedByAnotherSongAsync(
        WondarrDbContext database,
        long songId,
        string path,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);

        // Narrowed in SQL by the file name; the comparison itself is the platform's path comparison.
        var candidates = await database.SongFiles
            .AsNoTracking()
            .Where(file => file.SongId != songId && file.Path.EndsWith(name))
            .Select(file => file.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates.Any(other => PathRules.AreEqual(other, path));
    }

    /// <summary>
    /// Records a file a stopped import placed: the song's file row points at it, the item is imported,
    /// and the history says what happened. Nothing is saved when the file does not probe as audio.
    /// </summary>
    /// <param name="database">The database the rows are written through; the caller saves.</param>
    /// <param name="probe">Measures the landed file.</param>
    /// <param name="item">The item that was importing.</param>
    /// <param name="landedPath">The file <see cref="FindLandedFileAsync"/> found.</param>
    /// <param name="now">The recovery's time.</param>
    /// <param name="cancellationToken">Cancels the probe and the reads.</param>
    /// <returns><see langword="true"/> when the item was marked imported.</returns>
    internal static async Task<bool> RecordLandedFileAsync(
        WondarrDbContext database,
        IMediaProbe probe,
        QueueItem item,
        string landedPath,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var probed = await probe.ProbeAsync(landedPath, cancellationToken).ConfigureAwait(false);

        if (!probed.Decodable || probed.Info is not { } media)
        {
            return false;
        }

        var song = await database.Songs
            .Include(candidate => candidate.File)
            .FirstAsync(candidate => candidate.Id == item.SongId, cancellationToken)
            .ConfigureAwait(false);

        var upgraded = song.File is not null;
        var previousPath = song.File?.Path;
        var file = song.File ?? new SongFile { SongId = song.Id };

        file.Path = landedPath;
        file.Size = media.SizeBytes > 0 ? media.SizeBytes : new FileInfo(landedPath).Length;
        file.Codec = media.Codec;
        file.Container = media.Container;
        file.BitrateKbps = media.BitrateKbps;
        file.SampleRate = media.SampleRate;
        file.BitDepth = media.BitDepth;
        file.Channels = media.Channels;
        file.DurationMs = media.DurationMs;

        // Measured from the file that is there, as the import does. A YouTube grab is ranked as the
        // Opus stream it was whatever the library's rule turned it into (ADR-0008).
        file.QualityId = item.SourceType == SourceTypes.YouTube
            ? SeedData.Opus160QualityId
            : MeasuredQuality.FromMediaInfo(media);
        file.AcoustId = null;
        file.FingerprintVerified = false;
        file.SourceType = item.SourceType;
        file.SourceRef = JsonSerializer.Serialize(
            new { provider = item.Candidate.Provider, remotePath = item.Candidate.RemotePath, candidateId = item.CandidateId, queueItemId = item.Id, searchRunId = item.SearchRunId },
            Json);
        file.ImportedAt = now;
        file.TagsWritten = null;

        if (!upgraded)
        {
            database.SongFiles.Add(file);
        }

        database.History.Add(new HistoryItem
        {
            SongId = song.Id,
            EventType = upgraded ? HistoryEventType.Upgraded : HistoryEventType.Imported,
            SourceInstanceId = item.SourceInstanceId,
            QualityId = file.QualityId,
            Data = JsonSerializer.Serialize(
                new { path = landedPath, previousPath, recoveredAfterRestart = true },
                Json),
        });

        item.State = QueueItemState.Imported;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Progress = 1;
        item.NextCheckAt = null;
        item.Message = "recovered after restart: the file had already been placed in the library";

        return true;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Queue item {QueueItemId} was importing when the process stopped and its download is gone, but the song's destination could not be worked out; resetting it")]
    private static partial void NoDestination(ILogger logger, long queueItemId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Queue item {QueueItemId} was importing when the process stopped and {Count} new files sit at the song's destination; resetting it rather than guess")]
    private static partial void AmbiguousDestination(ILogger logger, long queueItemId, int count);
}
