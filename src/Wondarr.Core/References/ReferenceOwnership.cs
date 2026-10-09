using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.References;

/// <summary>
/// Makes an identified reference file the file of its song (LIBRARY_OUTPUT §7.6). The user's file is
/// never written, moved or deleted: Wondarr only records where it is, so the song counts as owned and
/// is never searched for again. The one reference <c>song_file</c> Wondarr ever deletes is the one that
/// points at a file it no longer identifies as that song.
/// </summary>
internal static class ReferenceOwnership
{
    /// <summary>The JSON stored in <c>song_file.source_ref</c> for a file owned through a reference library.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What happened to the song's file when the reference file was linked to it.</summary>
    /// <param name="Linked">Whether the reference file is now the song's file itself.</param>
    /// <param name="Message">The line the row carries, for example the duplicate notice, or <see langword="null"/>.</param>
    public sealed record LinkResult(bool Linked, string? Message)
    {
        /// <summary>The song already held a file of its own; the reference file is counted but not linked.</summary>
        public static readonly LinkResult Duplicate =
            new(false, "duplicate: the song already has a file");
    }

    /// <summary>The absolute path of a reference file on this machine.</summary>
    /// <param name="rootPath">The library's root.</param>
    /// <param name="relativePath">The row's <c>/</c>-separated path under the root.</param>
    public static string AbsolutePath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// How a song identified through a reference library is added: filed in the library the reference
    /// library adopts into, monitored, and marked as having come from that reference library. Automatic
    /// identification and a hand-made choice both use this, so the two cannot drift apart.
    /// </summary>
    /// <param name="library">The reference library the file belongs to.</param>
    /// <returns>The options the song service is called with.</returns>
    public static SongAddOptions AddOptions(ReferenceLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return new SongAddOptions
        {
            LibraryId = library.LibraryId,
            Monitored = true,
            AddedBy = string.Concat(
                "reference:",
                library.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),

            // The user owns the song in this very file: it is never searched for, and the song becomes
            // visible together with its file (CommitAsync), so a search cannot find it without one.
            SearchOnAdd = false,
            KeepTransactionOpen = true,
        };
    }

    /// <summary>
    /// Saves what the caller changed and commits the transaction the add left open, so the new songs and
    /// the reference files that own them reach every other connection in one step.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the changes are committed.</returns>
    public static async Task CommitAsync(WondarrDbContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (database.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (database.Database.CurrentTransaction is { } open)
            {
                await open.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Rolls back the transaction an add left open, when something after the add failed.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <returns>A task that completes when the transaction is rolled back.</returns>
    public static async Task RollbackAsync(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database.Database.CurrentTransaction is { } open)
        {
            await open.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives the song the reference file, unless it already has a file. An existing file at the same
    /// path is refreshed — the file changed on disk, the song did not — and any other file is left
    /// exactly as it is, because it is either a library file Wondarr manages or another reference
    /// file of the same recording.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="songId">The song the file was identified as.</param>
    /// <param name="absolutePath">The file's absolute path.</param>
    /// <param name="row">The reference file's row.</param>
    /// <param name="probe">What the scan measured about the file.</param>
    /// <param name="acoustId">The AcoustID the identification used, or <see langword="null"/>.</param>
    /// <param name="identifiedBy">Which tier identified the file; <c>acoustid</c> is the verified one.</param>
    /// <param name="now">The instant the file is recorded as imported.</param>
    /// <returns>What happened to the file row, and the message the reference row carries.</returns>
    public static async Task<LinkResult> LinkAsync(
        WondarrDbContext database,
        long songId,
        string absolutePath,
        ReferenceFile row,
        MediaInfo probe,
        string? acoustId,
        string identifiedBy,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(probe);

        var held = await HeldFileAsync(database, songId).ConfigureAwait(false);

        if (held is null)
        {
            var file = new SongFile { SongId = songId };

            Apply(file, absolutePath, row, probe, acoustId, identifiedBy, now);
            database.SongFiles.Add(file);
            database.History.Add(new HistoryItem
            {
                SongId = songId,
                EventType = HistoryEventType.Imported,
                Data = JsonSerializer.Serialize(
                    new ReferenceImport(row.ReferenceLibraryId, row.Id, absolutePath),
                    StoredJson),
            });

            return new LinkResult(true, null);
        }

        if (string.Equals(held.Path, absolutePath, StringComparison.OrdinalIgnoreCase))
        {
            // The same file, re-identified after it changed on disk: nothing about the song moved, only
            // what ffprobe measured, so the row follows the file.
            Apply(held, absolutePath, row, probe, acoustId, identifiedBy, now);

            return new LinkResult(true, null);
        }

        return LinkResult.Duplicate;
    }

    /// <summary>
    /// Frees a song whose reference file turned out to be another song, so it is wanted again. Only a
    /// reference <c>song_file</c> pointing at this exact path is deleted: a library file is Wondarr's
    /// own and a different reference file belongs to a file that is still that song.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="songId">The song the file used to be.</param>
    /// <param name="absolutePath">The reference file's absolute path.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    public static async Task<bool> ReleaseAsync(WondarrDbContext database, long songId, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(database);

        var held = await HeldFileAsync(database, songId).ConfigureAwait(false);

        if (held is null
            || !string.Equals(held.SourceType, SourceTypes.Reference, StringComparison.Ordinal)
            || !string.Equals(held.Path, absolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        database.SongFiles.Remove(held);

        return true;
    }

    /// <summary>
    /// The file a song currently holds. A file this run added a moment ago counts: a chunk saves once,
    /// so two reference files of one recording meet in the change tracker before either is in the
    /// database, and the second one has to see the first as the duplicate it is.
    /// </summary>
    private static async Task<SongFile?> HeldFileAsync(WondarrDbContext database, long songId)
    {
        var tracked = database.SongFiles.Local.FirstOrDefault(file =>
            file.SongId == songId && database.Entry(file).State != EntityState.Deleted);

        if (tracked is not null)
        {
            return tracked;
        }

        var stored = await database.SongFiles
            .FirstOrDefaultAsync(file => file.SongId == songId)
            .ConfigureAwait(false);

        // A row this chunk removed is still in the database until the save, and the query hands back
        // its tracked, deleted instance: the song holds nothing any more.
        return stored is not null && database.Entry(stored).State == EntityState.Deleted ? null : stored;
    }

    /// <summary>Writes what the probe measured and where the file came from onto a <c>song_file</c> row.</summary>
    private static void Apply(
        SongFile file,
        string absolutePath,
        ReferenceFile row,
        MediaInfo probe,
        string? acoustId,
        string identifiedBy,
        DateTime now)
    {
        file.Path = absolutePath;
        file.Size = row.Size;
        file.Codec = probe.Codec;
        file.Container = probe.Container;
        file.BitrateKbps = probe.BitrateKbps;
        file.SampleRate = probe.SampleRate;
        file.BitDepth = probe.BitDepth;
        file.Channels = probe.Channels;
        file.DurationMs = probe.DurationMs;
        file.QualityId = MeasuredQuality.FromMediaInfo(probe);
        file.AcoustId = acoustId;
        file.FingerprintVerified = string.Equals(identifiedBy, ReferenceIdentifier.AcoustIdTier, StringComparison.Ordinal);
        file.SourceType = SourceTypes.Reference;
        file.SourceRef = JsonSerializer.Serialize(
            new ReferenceSource(row.ReferenceLibraryId, row.Id),
            StoredJson);
        file.ImportedAt = now;

        // Nothing was written to the user's file, so there is no tag snapshot to record.
        file.TagsWritten = null;
    }

    /// <summary>The <c>source_ref</c> of a reference file: which library and which row owns it.</summary>
    private sealed record ReferenceSource(long ReferenceLibraryId, long ReferenceFileId);

    /// <summary>The payload of the <c>Imported</c> history row of a reference file.</summary>
    private sealed record ReferenceImport(long ReferenceLibraryId, long ReferenceFileId, string Path);
}
