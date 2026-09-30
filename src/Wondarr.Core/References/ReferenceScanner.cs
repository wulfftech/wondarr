using System.Globalization;
using System.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.References;

/// <summary>What one scan of a reference library did.</summary>
/// <param name="Seen">How many audio files the walk found.</param>
/// <param name="Added">How many of them were new rows.</param>
/// <param name="Changed">How many existing rows had a new size or modification time.</param>
/// <param name="Unchanged">How many existing rows matched on size and modification time.</param>
/// <param name="Missing">How many rows had no file on disk at the end of the scan.</param>
/// <param name="Unreadable">How many files were probed and found not to be decodable audio.</param>
public sealed record ReferenceScanResult(
    int Seen,
    int Added,
    int Changed,
    int Unchanged,
    int Missing,
    int Unreadable);

/// <summary>Walks a reference library and keeps one row per audio file (LIBRARY_OUTPUT §7.6).</summary>
public interface IReferenceScanner
{
    /// <summary>
    /// Scans one library: probes and reads every file whose size or modification time changed, marks the
    /// rows whose file has gone, and leaves everything else alone. Never writes to the folder.
    /// </summary>
    /// <param name="referenceLibraryId">The library to scan.</param>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the walk between files.</param>
    /// <returns>What the scan did.</returns>
    /// <exception cref="ReferenceLibraryUnavailableException">
    /// The library does not exist, its root cannot be listed, or the root looks like an unmounted share.
    /// No row was changed when this is thrown.
    /// </exception>
    Task<ReferenceScanResult> ScanAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// A reference library that cannot be scanned right now: the folder is gone, unreadable, or empty in a
/// way that means the share holding it is not mounted. Nothing was changed, so retrying is safe.
/// </summary>
public sealed class ReferenceLibraryUnavailableException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryUnavailableException"/> class.</summary>
    /// <param name="message">Why the library cannot be scanned.</param>
    public ReferenceLibraryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryUnavailableException"/> class.</summary>
    /// <param name="message">Why the library cannot be scanned.</param>
    /// <param name="innerException">The failure that made it unavailable.</param>
    public ReferenceLibraryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The incremental walk of a reference library. A file whose size and modification time are the ones
/// already recorded is not probed or read again, so a second scan of a 50 000-file library costs one
/// <c>stat</c> per file. The folder is only ever read.
/// </summary>
public sealed partial class ReferenceScanner(
    WondarrDbContext database,
    IMediaProbe probe,
    ITagReader tagReader,
    TimeProvider timeProvider,
    ILogger<ReferenceScanner> logger) : IReferenceScanner
{
    /// <summary>How many files are written to the database between saves.</summary>
    private const int SaveBatchSize = 100;

    /// <summary>The audio formats a reference library is expected to hold.</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus",
        ".wav", ".aif", ".aiff", ".wma", ".ape", ".wv",
    };

    /// <summary>
    /// Folders that are never part of a music library: NAS metadata, Mac resource forks, Windows
    /// recycle bins, and the system folder Windows keeps on every volume.
    /// </summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", ".AppleDouble", "$RECYCLE.BIN", "System Volume Information",
    };

    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc />
    public async Task<ReferenceScanResult> ScanAsync(
        long referenceLibraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken)
    {
        var library = await database.ReferenceLibraries
            .FirstOrDefaultAsync(row => row.Id == referenceLibraryId, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            throw new ReferenceLibraryUnavailableException(
                $"Reference library {referenceLibraryId.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        var root = library.RootPath;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw await UnavailableAsync(library, "the folder does not exist", cancellationToken)
                .ConfigureAwait(false);
        }

        var skipped = new List<string>();
        List<string> files;

        try
        {
            files = Walk(root, skipped, cancellationToken);
        }
        catch (ReferenceLibraryUnavailableException exception)
        {
            throw await UnavailableAsync(library, exception.Message, cancellationToken).ConfigureAwait(false);
        }

        // Untracked: a row joins the change tracker only when the scan touches it, and leaves it at the
        // next save, so a 50 000-file library never has 50 000 entities in every save's change detection.
        var rows = await database.ReferenceFiles
            .AsNoTracking()
            .Where(row => row.ReferenceLibraryId == referenceLibraryId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // An empty walk over a library that used to hold files means the share is not mounted, not that
        // every song was deleted: refusing here is what keeps a dropped mount from emptying the library.
        if (files.Count == 0 && rows.Exists(row => row.State != ReferenceFileState.Missing))
        {
            throw await UnavailableAsync(library, "no files found; is the share mounted?", cancellationToken)
                .ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var byPath = rows.ToDictionary(row => row.RelativePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var touched = new List<ReferenceFile>();

        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var unreadable = 0;
        var scanned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, file);
            FileInfo info;

            try
            {
                info = new FileInfo(file);
                _ = info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Listed a moment ago, so not gone: one that cannot be read now keeps its row as it is,
                // exactly like a file under a folder the walk could not list.
                LogEntrySkipped(logger, exception);
                skipped.Add(relative);
                continue;
            }

            seen.Add(relative);

            byPath.TryGetValue(relative, out var row);
            var isNew = row is null;

            if (row is null)
            {
                row = new ReferenceFile { ReferenceLibraryId = referenceLibraryId, RelativePath = relative };
                database.ReferenceFiles.Add(row);
            }
            else
            {
                row = Track(row);
            }

            touched.Add(row);

            var sameFile = !isNew && row.Size == info.Length && row.ModifiedAt == info.LastWriteTimeUtc;

            if (sameFile && row.State == ReferenceFileState.Unreadable)
            {
                // Probed again on every scan: "unreadable" is as often a probe that failed (ffprobe gone,
                // a timeout, a share that hiccuped) as a broken file, and the size and time alone would
                // otherwise keep the verdict until the file changed.
                await RescanAsync(row, file, info, now, isNew: false, cancellationToken).ConfigureAwait(false);

                unchanged++;

                if (row.State == ReferenceFileState.Unreadable)
                {
                    unreadable++;
                }
            }
            else if (sameFile)
            {
                row.LastSeenAt = now;

                // A file that came back with the same size and time was never really gone.
                if (row.State == ReferenceFileState.Missing)
                {
                    row.State = row.SongId is null ? ReferenceFileState.Pending : ReferenceFileState.Identified;
                    row.MissingSince = null;
                }

                unchanged++;
            }
            else
            {
                await RescanAsync(row, file, info, now, isNew, cancellationToken).ConfigureAwait(false);

                if (isNew)
                {
                    added++;
                }
                else
                {
                    changed++;
                }

                if (row.State == ReferenceFileState.Unreadable)
                {
                    unreadable++;
                }
            }

            scanned++;

            if (scanned % SaveBatchSize == 0)
            {
                await SaveAsync(touched, cancellationToken).ConfigureAwait(false);

                if (progress is not null)
                {
                    await progress(ProgressMessage(scanned, files.Count)).ConfigureAwait(false);
                }
            }
        }

        var missing = 0;

        foreach (var row in rows)
        {
            // An adopted file is the library's now (adoption copies it; a user who then deletes the
            // original has lost nothing), and a file under a folder the walk could not list was not
            // seen, which is not the same as gone.
            if (seen.Contains(row.RelativePath)
                || row.State == ReferenceFileState.Adopted
                || IsUnderSkipped(row.RelativePath, skipped))
            {
                continue;
            }

            missing++;

            if (row.State != ReferenceFileState.Missing)
            {
                var tracked = Track(row);
                tracked.State = ReferenceFileState.Missing;
                tracked.MissingSince = now;
                touched.Add(tracked);
            }
        }

        var result = new ReferenceScanResult(files.Count, added, changed, unchanged, missing, unreadable);

        library.LastScannedAt = now;
        library.LastScanMessage = Summary(result);

        await SaveAsync(touched, cancellationToken).ConfigureAwait(false);

        if (progress is not null)
        {
            await progress(ProgressMessage(scanned, files.Count)).ConfigureAwait(false);
        }

        LogScanned(logger, referenceLibraryId, files.Count, added, changed, missing);

        return result;
    }

    /// <summary>
    /// Re-reads one file: probe it, read its tags, and forget everything the last identification said
    /// about it. The song it was identified as is kept — the file is the same song, only its bytes moved.
    /// </summary>
    private async Task RescanAsync(
        ReferenceFile row,
        string path,
        FileInfo info,
        DateTime now,
        bool isNew,
        CancellationToken cancellationToken)
    {
        row.Size = info.Length;
        row.ModifiedAt = info.LastWriteTimeUtc;
        row.LastSeenAt = now;
        row.MissingSince = null;
        row.State = ReferenceFileState.Pending;
        row.Fingerprint = null;
        row.AcoustId = null;
        row.Confidence = 0;
        row.IdentifiedBy = null;
        row.Message = null;

        if (!isNew && row.Id != 0)
        {
            var stale = await database.MatchCandidates
                .Where(candidate => candidate.ReferenceFileId == row.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            database.MatchCandidates.RemoveRange(stale);
        }

        MediaProbeResult probed;

        try
        {
            probed = await probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            probed = new MediaProbeResult(false, null, exception.Message);
        }

        if (probed is { Decodable: true, Info: not null })
        {
            row.Probe = JsonSerializer.Serialize(probed.Info, StoredJson);
        }
        else
        {
            row.Probe = null;
            row.State = ReferenceFileState.Unreadable;
            row.Message = probed.Error ?? "the file is not decodable audio";
        }

        var tags = tagReader.Read(path);
        row.Tags = tags is null ? null : JsonSerializer.Serialize(tags, StoredJson);
    }

    /// <summary>
    /// The tracked instance of a row loaded without tracking: the one the context already holds (a
    /// caller's own context may), or the row itself, attached as unchanged.
    /// </summary>
    private ReferenceFile Track(ReferenceFile row)
    {
        var entry = database.ReferenceFiles.Local.FindEntry(row.Id);

        if (entry is not null)
        {
            return entry.Entity;
        }

        database.ReferenceFiles.Attach(row);

        return row;
    }

    /// <summary>
    /// Saves the rows written so far, so a long walk never holds one transaction open, and lets go of
    /// them: the scan never touches a row twice.
    /// </summary>
    private async Task SaveAsync(List<ReferenceFile> touched, CancellationToken cancellationToken)
    {
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in touched)
        {
            database.Entry(row).State = EntityState.Detached;
        }

        touched.Clear();
    }

    /// <summary>
    /// Records why the library cannot be scanned and rethrows. Nothing else is written: the caller sees
    /// exactly the rows it had before.
    /// </summary>
    private async Task<ReferenceLibraryUnavailableException> UnavailableAsync(
        ReferenceLibrary library,
        string reason,
        CancellationToken cancellationToken)
    {
        library.LastScanMessage = reason;

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogUnavailable(logger, library.Id, reason);

        return new ReferenceLibraryUnavailableException($"'{library.RootPath}' cannot be scanned: {reason}");
    }

    /// <summary>
    /// Every audio file under <paramref name="root"/>, in ordinal path order. Directory symlinks and
    /// junctions are never followed, so a link that points back up the tree cannot make the walk loop.
    /// </summary>
    private List<string> Walk(string root, List<string> skipped, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        Collect(root, root, files, skipped, isRoot: true, cancellationToken);

        files.Sort(StringComparer.Ordinal);

        return files;
    }

    /// <summary>
    /// Whether a row's file lies in (or is) an entry the walk had to skip. Such a row keeps its state:
    /// the file was not seen, but nothing says it is gone.
    /// </summary>
    /// <param name="relativePath">The row's <c>/</c>-separated path under the root.</param>
    /// <param name="skipped">The <c>/</c>-separated paths of the entries the walk could not read.</param>
    internal static bool IsUnderSkipped(string relativePath, IReadOnlyList<string> skipped)
    {
        foreach (var entry in skipped)
        {
            if (string.Equals(relativePath, entry, StringComparison.Ordinal)
                || (relativePath.Length > entry.Length
                    && relativePath[entry.Length] == '/'
                    && relativePath.StartsWith(entry, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private void Collect(
        string root,
        string directory,
        List<string> files,
        List<string> skipped,
        bool isRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A recycle bin is our own storage, not the user's music; the marker is what identifies one.
        if (File.Exists(Path.Combine(directory, RecycleBin.MarkerFileName)))
        {
            return;
        }

        string[] entries;

        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            if (isRoot)
            {
                // The root not being listable is not a partially readable library: it is unavailable.
                throw new ReferenceLibraryUnavailableException(
                    $"the folder cannot be listed: {exception.Message}", exception);
            }

            LogFolderSkipped(logger, exception);
            skipped.Add(Relative(root, directory));
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);

            if (name.StartsWith('.') || IgnoredFolders.Contains(name))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // A symlinked folder or junction can point anywhere, including back at an
                        // ancestor. A symlinked file is read like any other: reading it cannot loop.
                        continue;
                    }

                    Collect(root, entry, files, skipped, isRoot: false, cancellationToken);
                }
                else if (AudioExtensions.Contains(Path.GetExtension(entry)))
                {
                    files.Add(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // One unreadable entry never ends the walk.
                LogEntrySkipped(logger, exception);
                skipped.Add(Relative(root, entry));
            }
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string ProgressMessage(int scanned, int total) =>
        string.Concat(
            "Scanned ",
            scanned.ToString(CultureInfo.InvariantCulture),
            " of ",
            total.ToString(CultureInfo.InvariantCulture),
            " files");

    private static string Summary(ReferenceScanResult result) =>
        string.Concat(
            result.Seen.ToString(CultureInfo.InvariantCulture),
            " files: ",
            result.Added.ToString(CultureInfo.InvariantCulture),
            " added, ",
            result.Changed.ToString(CultureInfo.InvariantCulture),
            " changed, ",
            result.Unchanged.ToString(CultureInfo.InvariantCulture),
            " unchanged, ",
            result.Missing.ToString(CultureInfo.InvariantCulture),
            " missing, ",
            result.Unreadable.ToString(CultureInfo.InvariantCulture),
            " unreadable");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped a file that could not be read.")]
    private static partial void LogEntrySkipped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a folder of the reference library that could not be listed.")]
    private static partial void LogFolderSkipped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reference library {ReferenceLibraryId} cannot be scanned: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, long referenceLibraryId, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Scanned reference library {ReferenceLibraryId}: {Seen} files, {Added} added, {Changed} changed, {Missing} missing")]
    private static partial void LogScanned(ILogger logger, long referenceLibraryId, int seen, int added, int changed, int missing);
}