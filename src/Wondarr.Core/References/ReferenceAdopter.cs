using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.References;

/// <summary>How one adoption run ended.</summary>
/// <param name="Adopted">Files handed over to the target library.</param>
/// <param name="Skipped">Identified files whose song already holds a file of its own, left as they are.</param>
/// <param name="Failed">Files that could not be copied, or whose copy could not be recorded.</param>
public sealed record ReferenceAdoptResult(int Adopted, int Skipped, int Failed);

/// <summary>
/// Adoption (LIBRARY_OUTPUT §7.6): the identified files of an <see cref="ReferenceLibraryMode.Adopt"/>
/// reference library are handed over to a managed library. Each one is <em>copied</em> — the user's
/// original is never written, moved or deleted — through the same organizer imports go through, so the
/// copy carries the Picard tag set, the library's layout and the album policy; the song's file row is
/// repointed at the copy and the reference row becomes <see cref="ReferenceFileState.Adopted"/>.
/// </summary>
public interface IReferenceAdopter
{
    /// <summary>Adopts the identified files of one reference library.</summary>
    /// <param name="referenceLibraryId">The library whose identified files are adopted.</param>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the run between files.</param>
    /// <returns>How the run's files ended.</returns>
    /// <exception cref="InvalidOperationException">The reference library, or its target library, does not exist.</exception>
    Task<ReferenceAdoptResult> AdoptAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken);
}

/// <summary>The one implementation of <see cref="IReferenceAdopter"/>.</summary>
/// <remarks>
/// One file is one save: a crash between two files never leaves a half-recorded batch, and the row of a
/// file that failed stays <see cref="ReferenceFileState.Identified"/> so the next run retries it.
/// </remarks>
public sealed partial class ReferenceAdopter(
    WondarrDbContext database,
    ILibraryOrganizer organizer,
    IEventAggregator events,
    TimeProvider timeProvider,
    ILogger<ReferenceAdopter> logger) : IReferenceAdopter
{
    /// <summary>How many adopted files there are between two progress messages.</summary>
    internal const int ProgressEvery = 25;

    /// <summary>The JSON shape of every payload this class writes: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The probe is stored as camelCase JSON, the way the scan wrote it.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<ReferenceAdoptResult> AdoptAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken)
    {
        var library = await database.ReferenceLibraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == referenceLibraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat(
                    "Reference library ",
                    referenceLibraryId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        // A library that only lends its files to the ownership records is read-only: nothing is adopted.
        if (library.Mode != ReferenceLibraryMode.Adopt || library.LibraryId is not { } targetLibraryId)
        {
            return new ReferenceAdoptResult(0, 0, 0);
        }

        var target = await database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == targetLibraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat(
                    "The target library ",
                    targetLibraryId.ToString(CultureInfo.InvariantCulture),
                    " of reference library ",
                    referenceLibraryId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        var rows = await database.ReferenceFiles
            .AsNoTracking()
            .Where(candidate => candidate.ReferenceLibraryId == referenceLibraryId
                && candidate.State == ReferenceFileState.Identified
                && candidate.SongId != null)
            .OrderBy(candidate => candidate.RelativePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var adopted = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolutePath = ReferenceOwnership.AbsolutePath(library.RootPath, row.RelativePath);

            // The song is loaded exactly as the import loads it: the album context decides the folder
            // and the tags, and the credits are what the tag writer writes.
            var song = await LoadSongAsync(row.SongId!.Value, cancellationToken).ConfigureAwait(false);
            var file = song?.File;

            // Only the file this row owns is adopted. A song that has moved on — it was downloaded
            // since, or another file of the same recording satisfies it — keeps what it has, and this
            // row is left as it is.
            if (song is null || file is null || !Owns(row, absolutePath, file))
            {
                LogNotAdoptable(logger, row.Id, absolutePath);
                database.ChangeTracker.Clear();
                skipped++;
                continue;
            }

            var album = song.AlbumContext;

            if (album is null)
            {
                failed++;
                await MarkFailedAsync(row.Id, "the song has no album context", cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Nothing is probed again: the scan measured the file, and the copy is byte for byte the
            // file it measured.
            var media = Read<MediaInfo>(row.Probe);

            if (media is null)
            {
                failed++;
                await MarkFailedAsync(row.Id, "the file carries no probe to file it from", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var measured = MeasuredQuality.FromMediaInfo(media);

            var quality = await database.Qualities
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == measured, cancellationToken)
                .ConfigureAwait(false);

            if (quality is null)
            {
                failed++;
                await MarkFailedAsync(
                        row.Id,
                        string.Concat(
                            "there is no quality row for measured quality ",
                            measured.ToString(CultureInfo.InvariantCulture)),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // The song is being filed in the target library, so it belongs to it from here on.
            if (song.LibraryId != target.Id)
            {
                song.LibraryId = target.Id;
            }

            var credits = song.Artists
                .OrderBy(credit => credit.Position)
                .Select(credit => (credit.Artist, credit.Role))
                .ToList();

            var extension = Path.GetExtension(absolutePath).TrimStart('.').ToLowerInvariant();

            // KeepSource is the whole promise of adoption: the organizer stages a copy of the file and
            // files that, and the user's own file is only ever read.
            OrganizeResult placement;

            try
            {
                placement = await organizer
                .OrganizeAsync(
                    new OrganizeRequest(
                        song,
                        album,
                        credits,
                        target,
                        absolutePath,
                        extension,
                        media,
                        quality,
                        SourceTypes.Adopted,
                        row.AcoustId ?? file.AcoustId,
                        KeepSource: true,
                        ReplacesPath: null),
                    cancellationToken)
                .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The organizer throws for what it cannot even start on — the original moved or
                // deleted since the scan (the staging copy fails), a song without credits. That file
                // fails; the files behind it are still adopted.
                LogOrganizeFailed(logger, row.Id, exception);
                database.ChangeTracker.Clear();

                failed++;
                await MarkFailedAsync(row.Id, exception.Message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!placement.Success || placement.FinalPath is null)
            {
                // The organizer corrects the tracked album context before it does anything that can
                // fail, and that correction must not be saved with a failed file (see its doc comment):
                // the tracker is cleared before the reference row is written back.
                database.ChangeTracker.Clear();

                failed++;
                await MarkFailedAsync(
                        row.Id,
                        placement.Error ?? "the organizer gave no final path",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var finalPath = placement.FinalPath;

            try
            {
                // The copy is a library file now and the song's own file row points at it.
                file.Path = finalPath;
                file.Size = new FileInfo(finalPath).Length;
                file.Codec = media.Codec;
                file.Container = media.Container;
                file.BitrateKbps = media.BitrateKbps;
                file.SampleRate = media.SampleRate;
                file.BitDepth = media.BitDepth;
                file.Channels = media.Channels;
                file.DurationMs = media.DurationMs;
                file.QualityId = measured;
                file.AcoustId = row.AcoustId ?? file.AcoustId;
                file.SourceType = SourceTypes.Adopted;
                file.SourceRef = JsonSerializer.Serialize(
                    new AdoptedSource(library.Id, row.Id, absolutePath),
                    Json);
                file.ImportedAt = timeProvider.GetUtcNow().UtcDateTime;
                file.TagsWritten = JsonSerializer.Serialize(placement.TagsWritten, Json);

                database.History.Add(new HistoryItem
                {
                    SongId = song.Id,
                    EventType = HistoryEventType.Imported,
                    Data = JsonSerializer.Serialize(new AdoptedHistoryData(finalPath, absolutePath), Json),
                });

                var adoptedRow = await database.ReferenceFiles
                    .FirstAsync(candidate => candidate.Id == row.Id, cancellationToken)
                    .ConfigureAwait(false);

                adoptedRow.State = ReferenceFileState.Adopted;
                adoptedRow.Message = null;

                // One save per file: whatever happens next, this one is recorded whole or not at all.
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The copy is in the library already; only the bookkeeping failed. The copy stays where
                // it is, and the row says so, so the user can see what happened rather than adopt it twice.
                LogRecordFailed(logger, row.Id, exception);
                database.ChangeTracker.Clear();

                failed++;
                await MarkFailedAsync(
                        row.Id,
                        string.Concat("copied to ", finalPath, " but recording it failed: ", exception.Message),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            adopted++;

            var songId = song.Id;
            var fileId = file.Id;

            // Every file is saved on its own and never touched again, so the tracker lets go of it:
            // a library of tens of thousands of files would otherwise make every save slower.
            database.ChangeTracker.Clear();

            // The share rescan and the Plex partial scan follow a song that gained a library file,
            // exactly as they do after an import.
            await events
                .PublishAsync(new SongImportedEvent(songId, fileId, Upgraded: false), cancellationToken)
                .ConfigureAwait(false);

            if (progress is not null && adopted % ProgressEvery == 0)
            {
                await progress(
                        string.Concat(
                            "Adopted ",
                            adopted.ToString(CultureInfo.InvariantCulture),
                            " of ",
                            rows.Count.ToString(CultureInfo.InvariantCulture),
                            " files"))
                    .ConfigureAwait(false);
            }
        }

        return new ReferenceAdoptResult(adopted, skipped, failed);
    }

    /// <summary>Loads the song with everything filing it needs, as <c>ImportService</c> loads it.</summary>
    private async Task<Song?> LoadSongAsync(long songId, CancellationToken cancellationToken) =>
        await database.Songs
            .Include(song => song.AlbumContext)
            .Include(song => song.File)
            .Include(song => song.Artists)
                .ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Whether the song's file is still the reference file this row describes.</summary>
    private static bool Owns(ReferenceFile row, string absolutePath, SongFile file) =>
        string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal)
        && string.Equals(file.Path, absolutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes why one file could not be adopted onto its row, which stays
    /// <see cref="ReferenceFileState.Identified"/> so the next run tries it again.
    /// </summary>
    private async Task MarkFailedAsync(long referenceFileId, string error, CancellationToken cancellationToken)
    {
        var row = await database.ReferenceFiles
            .FirstAsync(candidate => candidate.Id == referenceFileId, cancellationToken)
            .ConfigureAwait(false);

        row.Message = string.Concat("adoption failed: ", error);

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
    }

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

    /// <summary>The <c>source_ref</c> of an adopted file: where it came from.</summary>
    private sealed record AdoptedSource(long ReferenceLibraryId, long ReferenceFileId, string OriginalPath);

    /// <summary>The payload of the <c>Imported</c> history row of an adopted file.</summary>
    private sealed record AdoptedHistoryData(string Path, string AdoptedFrom);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Reference file {ReferenceFileId} ({Path}) is not the file its song holds; left as it is.")]
    private static partial void LogNotAdoptable(ILogger logger, long referenceFileId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Adopting reference file {ReferenceFileId} failed; the next file is still adopted")]
    private static partial void LogOrganizeFailed(ILogger logger, long referenceFileId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the adoption of reference file {ReferenceFileId} failed")]
    private static partial void LogRecordFailed(ILogger logger, long referenceFileId, Exception exception);
}
