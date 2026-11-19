using System.Globalization;
using System.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;

namespace Wondarr.Core.MusicVideos;

/// <summary>What one scan of the video folder did.</summary>
/// <param name="Seen">How many video files the walk found.</param>
/// <param name="Added">How many of them were new rows.</param>
/// <param name="Changed">How many existing rows had a new size or modification time.</param>
/// <param name="Unchanged">How many existing rows matched on size and modification time.</param>
/// <param name="Missing">How many rows had no file on disk at the end of the scan.</param>
/// <param name="Probed">How many files were probed (new, changed, or never read successfully).</param>
/// <param name="Released">How many rows stayed missing past the grace period and were let go.</param>
/// <param name="MountProblem">Whether so many files were missing at once that the scan released nothing.</param>
public sealed record MusicVideoScanResult(
    int Seen,
    int Added,
    int Changed,
    int Unchanged,
    int Missing,
    int Probed,
    int Released = 0,
    bool MountProblem = false);

/// <summary>The video folder cannot be scanned right now. Nothing was changed, so trying again later is safe.</summary>
public sealed class MusicVideoScanUnavailableException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoScanUnavailableException"/> class.</summary>
    /// <param name="message">Why the folder cannot be scanned.</param>
    public MusicVideoScanUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The video root was changed while a scan ran, so what the scan read describes the old root. Nothing was saved; the
/// caller scans again.
/// </summary>
public sealed class MusicVideoScanRootChangedException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoScanRootChangedException"/> class.</summary>
    public MusicVideoScanRootChangedException()
        : base("the video folder was changed while it was being scanned")
    {
    }
}

/// <summary>Walks the video root and keeps one <c>music_video_file</c> row per video file.</summary>
public interface IMusicVideoFileScanner
{
    /// <summary>
    /// Scans the video root: probes and reads the <c>.nfo</c> of every file that is new or whose size or modification time
    /// changed, marks the rows whose file has gone, and lets a row go only after it stayed missing through a later scan
    /// and 24 hours. Never writes to the folder.
    /// </summary>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the scan between files.</param>
    /// <exception cref="MusicVideoScanUnavailableException">
    /// The feature is off, the root is not set or not ready (no marker), cannot be listed, or looks unmounted. Nothing
    /// was changed.
    /// </exception>
    Task<MusicVideoScanResult> ScanAsync(Func<string, Task>? progress, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoFileScanner : IMusicVideoFileScanner
{
    /// <summary>The most probes that run at once, so a share is never hammered.</summary>
    public const int MaxConcurrentProbes = 2;

    /// <summary>How many rows are written between saves.</summary>
    private const int SaveBatchSize = 100;

    /// <summary>The containers a video may be in.</summary>
    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".mpg", ".mpeg", ".wmv", ".ts",
    };

    /// <summary>Folders that are never the user's videos: NAS thumbnail stores, bins and volume metadata.</summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", ".AppleDouble", "$RECYCLE.BIN", "System Volume Information",
    };

    private readonly WondarrDbContext _database;
    private readonly IMediaProbe _probe;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly WondarrPaths _paths;
    private readonly ILibraryRoots _roots;
    private readonly TimeProvider _time;
    private readonly ILogger<MusicVideoFileScanner> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoFileScanner"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="probe">Probes a video for its streams, length and tags.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the root and the naming template.</param>
    /// <param name="importOptions">Where the recycle bin is, which the walk leaves out.</param>
    /// <param name="paths">Where Wondarr's data directory is, for the root rules.</param>
    /// <param name="roots">Checks the root's marker.</param>
    /// <param name="time">The clock the missing grace runs on.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoFileScanner(
        WondarrDbContext database,
        IMediaProbe probe,
        IOptionsMonitor<MusicVideoOptions> options,
        IOptionsMonitor<ImportOptions> importOptions,
        WondarrPaths paths,
        ILibraryRoots roots,
        TimeProvider time,
        ILogger<MusicVideoFileScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _probe = probe;
        _options = options;
        _importOptions = importOptions;
        _paths = paths;
        _roots = roots;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MusicVideoScanResult> ScanAsync(Func<string, Task>? progress, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            throw new MusicVideoScanUnavailableException("music videos are turned off");
        }

        var root = options.RootPath;

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new MusicVideoScanUnavailableException("no video folder is set");
        }

        var problems = await MusicVideoRootRules
            .ProblemsAsync(root, _database, _paths, _importOptions, cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count > 0)
        {
            throw new MusicVideoScanUnavailableException($"the video folder cannot be used: {string.Join(' ', problems)}");
        }

        var status = await _roots.CheckAsync(root, cancellationToken).ConfigureAwait(false);

        if (status != LibraryRootStatus.Ready)
        {
            throw new MusicVideoScanUnavailableException(status switch
            {
                LibraryRootStatus.Missing => "the video folder does not exist",
                LibraryRootStatus.Unmarked => $"the video folder has no {LibraryRoots.MarkerFileName} file, so it may be an unmounted share",
                _ => "the video folder did not answer in time",
            });
        }

        var recycleBin = _importOptions.CurrentValue.ResolveRecycleBinPath(_paths);

        Walked walked;

        try
        {
            walked = await WalkAsync(root, recycleBin, cancellationToken).ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            throw new MusicVideoScanUnavailableException("the video folder did not answer in time");
        }

        var allRows = await _database.MusicVideoFiles.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Rows outside the current root are not part of this walk: the root was changed, and they are let go below.
        var rows = allRows.Where(row => InRoot(root, row.Path)).ToList();
        var outside = allRows.Where(row => !InRoot(root, row.Path)).ToList();

        // An empty walk over a folder that used to hold videos means the share is not mounted, not that every video was
        // deleted: refusing here is what keeps a dropped mount from emptying the table.
        // A row the user ignored carries no consequence, so it does not make an empty folder suspicious.
        if (walked.Files.Count == 0 && rows.Exists(row => !IsMissing(row) && row.State != MusicVideoFileState.Ignored))
        {
            throw new MusicVideoScanUnavailableException("no videos found; is the share mounted?");
        }

        // The files Wondarr downloaded are in the folder too, and are not the scan's business.
        var downloaded = (await _database.MusicVideos
                .AsNoTracking()
                .Where(video => video.FoundBy != MusicVideoFoundBy.Existing && video.Path != null)
                .Select(video => video.Path!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var found = walked.Files.Where(file => !downloaded.Contains(file.Path)).ToList();
        var byPath = rows.ToDictionary(row => row.Path, StringComparer.Ordinal);
        var now = _time.GetUtcNow().UtcDateTime;

        // What has to be probed: new files, changed files, and files that never read successfully.
        // A file whose only change is its .nfo is not probed again, just read again.
        var work = new List<Work>();

        foreach (var file in found)
        {
            if (!byPath.TryGetValue(file.Path, out var row) || !Same(row, file) || row.Container is null)
            {
                work.Add(new Work(file, Probe: true));
            }
            else if (row.NfoModifiedAt != file.NfoModifiedAt)
            {
                work.Add(new Work(file, Probe: false));
            }
        }

        var read = await ReadAsync(work, progress, cancellationToken).ConfigureAwait(false);

        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in found)
        {
            cancellationToken.ThrowIfCancellationRequested();

            seen.Add(file.Path);
            byPath.TryGetValue(file.Path, out var row);

            if (row is not null && Same(row, file) && row.Container is not null && row.NfoModifiedAt == file.NfoModifiedAt)
            {
                Restore(row);
                unchanged++;

                continue;
            }

            if (!read.TryGetValue(file.Path, out var result))
            {
                // Its probe or its .nfo timed out; the row (if any) is left as it is, and the next scan asks again.
                if (row is not null)
                {
                    Restore(row);
                }

                continue;
            }

            if (row is null)
            {
                row = new MusicVideoFile { Path = file.Path };
                _database.MusicVideoFiles.Add(row);
                byPath[file.Path] = row;
                rows.Add(row);
                added++;
            }
            else if (!Same(row, file))
            {
                changed++;
            }
            else
            {
                unchanged++;
            }

            await ApplyAsync(row, file, result, root, options.NamingTemplate, cancellationToken).ConfigureAwait(false);
            row.NfoModifiedAt = file.NfoModifiedAt;
        }

        var missing = 0;
        var stillMissing = new List<MusicVideoFile>();

        foreach (var row in rows)
        {
            if (seen.Contains(row.Path))
            {
                continue;
            }

            // A file under a folder the walk could not list was not seen, which is not the same as gone.
            if (ReferenceScanner.IsUnderSkipped(RelativeKey(root, row.Path), walked.Skipped))
            {
                continue;
            }

            // A file that is excluded now (Wondarr's own download) is none of the scan's business either.
            if (downloaded.Contains(row.Path))
            {
                continue;
            }

            missing++;

            if (IsMissing(row))
            {
                stillMissing.Add(row);

                continue;
            }

            row.MissingSince = now;

            // The user's own verdict (Ignored) survives the file going away for a while; every other state becomes Missing.
            if (row.State != MusicVideoFileState.Ignored)
            {
                row.State = MusicVideoFileState.Missing;
            }
        }

        // A share that is half unmounted looks like half the folder being deleted at once, so it releases nothing.
        var mountProblem = ReferenceMissingRules.IsMountProblem(rows.Count, missing);
        var released = 0;

        if (mountProblem)
        {
            LogMountProblem(_logger, missing, rows.Count);
        }
        else
        {
            foreach (var row in stillMissing)
            {
                if (row.MissingSince is not { } since || now - since < ReferenceMissingRules.Grace)
                {
                    continue;
                }

                await ReleaseAsync(row, cancellationToken).ConfigureAwait(false);
                released++;
            }
        }

        // A root changed on purpose is not a mount problem, so the rows of the old root go whatever the guard says.
        foreach (var row in outside)
        {
            await ReleaseAsync(row, cancellationToken).ConfigureAwait(false);
            released++;
        }

        // The root may have been changed while the walk and the probes ran: what was read describes the old root, so it is
        // all thrown away (nothing was saved yet) and the caller scans again.
        if (!string.Equals(_options.CurrentValue.RootPath, root, StringComparison.Ordinal))
        {
            _database.ChangeTracker.Clear();

            throw new MusicVideoScanRootChangedException();
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (progress is not null)
        {
            await progress(string.Create(CultureInfo.InvariantCulture, $"Scanned {found.Count} videos")).ConfigureAwait(false);
        }

        LogScanned(_logger, found.Count, added, changed, missing);

        return new MusicVideoScanResult(found.Count, added, changed, unchanged, missing, work.Count, released, mountProblem);
    }

    /// <summary>
    /// Whether the row counts as missing: its file was not found by an earlier scan. A row the user ignored keeps its
    /// state and shows it by <see cref="MusicVideoFile.MissingSince"/> alone.
    /// </summary>
    internal static bool IsMissing(MusicVideoFile row) =>
        row.State == MusicVideoFileState.Missing
        || (row.State == MusicVideoFileState.Ignored && row.MissingSince is not null);

    private static bool Same(MusicVideoFile row, Found file) =>
        row.Size == file.Size && row.ModifiedAt == file.ModifiedAt;

    /// <summary>A file that came back with the same size and time was never really gone.</summary>
    private static void Restore(MusicVideoFile row)
    {
        if (row.State == MusicVideoFileState.Missing)
        {
            // An adopted file that went missing still holds its song; any other had no verdict worth keeping.
            row.State = row.SongId is null ? MusicVideoFileState.Pending : MusicVideoFileState.Adopted;
        }
        else if (row is { State: MusicVideoFileState.Adopted, SongId: null })
        {
            // Its song was deleted: the file is still here and is matched again.
            row.State = MusicVideoFileState.Pending;
        }

        row.MissingSince = null;
    }

    /// <summary>Writes what was read into the row, and decides whether the file is identified again.</summary>
    private async Task ApplyAsync(
        MusicVideoFile row,
        Found file,
        ReadResult result,
        string root,
        string namingTemplate,
        CancellationToken cancellationToken)
    {
        var wasChanged = row.Id != 0 && !Same(row, file);
        var wasUnread = row.Container is null;
        var previous = row.State;

        row.Size = file.Size;
        row.ModifiedAt = file.ModifiedAt;
        row.MissingSince = null;

        if (result.Probe is { } info)
        {
            row.Container = string.IsNullOrWhiteSpace(info.Container) ? Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant() : info.Container;
            row.DurationMs = info.DurationMs > 0 ? info.DurationMs : null;
            row.Width = info.Width;
            row.Height = info.Height;
            row.TagTitle = info.TagTitle;
            row.TagArtist = info.TagArtist;
        }
        else if (wasChanged)
        {
            // The old measurements describe a file that is no longer there.
            row.Container = null;
            row.DurationMs = null;
            row.Width = null;
            row.Height = null;
            row.TagTitle = null;
            row.TagArtist = null;
        }

        row.NfoTitle = result.Nfo?.Title;
        row.NfoArtist = result.Nfo?.Artist;

        var names = MusicVideoNameParser.Parse(RelativeKey(root, file.Path), namingTemplate);
        var first = names.Count > 0 ? names[0] : null;
        row.ParsedArtist = first?.Artist;
        row.ParsedTitle = first?.Title;

        if (row.Id == 0 && _database.Entry(row).State == EntityState.Added)
        {
            row.State = MusicVideoFileState.Pending;

            return;
        }

        switch (previous)
        {
            case MusicVideoFileState.Ignored:
                // The user's choice outlives a changed file.
                return;

            case MusicVideoFileState.Adopted or MusicVideoFileState.Missing when row.SongId is not null:
                if (wasChanged)
                {
                    // Identified again: the link goes, and the file is matched afresh (it will most likely be the same song).
                    await UnlinkAsync(row, cancellationToken).ConfigureAwait(false);
                    row.State = MusicVideoFileState.Pending;
                }
                else
                {
                    // Only a first successful read: the file is still the one that was adopted.
                    row.State = MusicVideoFileState.Adopted;
                    await RefreshAdoptedAsync(row, cancellationToken).ConfigureAwait(false);
                }

                return;

            default:
                if (wasChanged || wasUnread || previous is MusicVideoFileState.Missing or MusicVideoFileState.Adopted)
                {
                    row.State = MusicVideoFileState.Pending;
                    row.SongId = null;
                }

                return;
        }
    }

    /// <summary>Takes the <see cref="MusicVideoFoundBy.Existing"/> row of this file away from its song.</summary>
    private async Task UnlinkAsync(MusicVideoFile row, CancellationToken cancellationToken)
    {
        var video = await _database.MusicVideos
            .FirstOrDefaultAsync(
                candidate => candidate.FoundBy == MusicVideoFoundBy.Existing && candidate.Path == row.Path,
                cancellationToken)
            .ConfigureAwait(false);

        if (video is not null)
        {
            _database.MusicVideos.Remove(video);
        }

        row.SongId = null;
    }

    /// <summary>An adopted file that was measured again keeps its link, and its song's row follows the measurement.</summary>
    private async Task RefreshAdoptedAsync(MusicVideoFile row, CancellationToken cancellationToken)
    {
        var video = await _database.MusicVideos
            .FirstOrDefaultAsync(
                candidate => candidate.FoundBy == MusicVideoFoundBy.Existing && candidate.Path == row.Path,
                cancellationToken)
            .ConfigureAwait(false);

        if (video is null)
        {
            return;
        }

        video.Width = row.Width;
        video.Height = row.Height;
        video.DurationMs = row.DurationMs;
        video.SizeBytes = row.Size > 0 ? row.Size : null;
    }

    /// <summary>
    /// Lets a row go: the song's <c>music_video</c> row is deleted only when it is this file's own
    /// (<see cref="MusicVideoFoundBy.Existing"/> at this path), then the file's row.
    /// </summary>
    private async Task ReleaseAsync(MusicVideoFile row, CancellationToken cancellationToken)
    {
        var video = await _database.MusicVideos
            .FirstOrDefaultAsync(
                candidate => candidate.FoundBy == MusicVideoFoundBy.Existing && candidate.Path == row.Path,
                cancellationToken)
            .ConfigureAwait(false);

        if (video is not null)
        {
            _database.MusicVideos.Remove(video);
            LogReleased(_logger, row.Path, video.SongId);
        }

        _database.MusicVideoFiles.Remove(row);
    }

    /// <summary>
    /// Probes the files and reads their <c>.nfo</c>, at most <see cref="MaxConcurrentProbes"/> at a time. A file whose probe
    /// or <c>.nfo</c> read timed out has no entry, and is asked again by the next scan; a file that is not readable has an
    /// entry without a probe.
    /// </summary>
    private async Task<Dictionary<string, ReadResult>> ReadAsync(
        List<Work> work,
        Func<string, Task>? progress,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, ReadResult>(StringComparer.Ordinal);
        var gate = new object();
        var done = 0;

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentProbes, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var file = item.File;
                ReadResult? result;

                try
                {
                    result = await ReadOneAsync(file, item.Probe, token).ConfigureAwait(false);
                }
                catch (MusicVideoTimeoutException)
                {
                    result = null;
                }

                int count;

                lock (gate)
                {
                    if (result is not null)
                    {
                        results[file.Path] = result;
                    }

                    count = ++done;
                }

                if (progress is not null && count % SaveBatchSize == 0)
                {
                    await progress(string.Create(CultureInfo.InvariantCulture, $"Read {count} of {work.Count} videos")).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        return results;
    }

    private async Task<ReadResult> ReadOneAsync(Found file, bool probeFile, CancellationToken cancellationToken)
    {
        var nfoPath = Path.ChangeExtension(file.Path, ".nfo");
        var nfo = await MusicVideoDisk
            .RunAsync(() => MusicVideoNfo.ReadFile(nfoPath), cancellationToken)
            .ConfigureAwait(false);

        if (!probeFile)
        {
            return new ReadResult(null, nfo);
        }

        var probe = await _probe.ProbeVideoAsync(file.Path, cancellationToken).ConfigureAwait(false);

        if (probe.TimedOut)
        {
            // A tool that ran out of time says nothing about the file.
            throw new MusicVideoTimeoutException();
        }

        return new ReadResult(probe is { Readable: true, Info: { VideoStreams: > 0 } info } ? info : null, nfo);
    }

    /// <summary>Every video file under the root, bounded per folder, and the folders that could not be listed.</summary>
    private async Task<Walked> WalkAsync(string root, string recycleBin, CancellationToken cancellationToken)
    {
        var files = new List<Found>();
        var skipped = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();
            Listing listing;

            try
            {
                listing = await MusicVideoDisk.RunAsync(() => List(directory, recycleBin), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                if (string.Equals(directory, root, StringComparison.Ordinal))
                {
                    throw new MusicVideoScanUnavailableException($"the video folder cannot be listed: {exception.Message}");
                }

                LogFolderSkipped(_logger, exception);
                skipped.Add(RelativeKey(root, directory));

                continue;
            }

            files.AddRange(listing.Files);

            foreach (var child in listing.Directories)
            {
                pending.Push(child);
            }
        }

        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

        return new Walked(files, skipped);
    }

    /// <summary>Lists one folder: its video files with their size and time, and its subfolders worth walking.</summary>
    private static Listing List(string directory, string recycleBin)
    {
        var info = new DirectoryInfo(directory);
        var files = new List<Found>();
        var directories = new List<string>();
        var entries = info.EnumerateFileSystemInfos().ToList();

        // The .nfo beside a video is part of what identifies it: its time is noted, so an edit is read again.
        var nfoTimes = entries
            .Where(entry => entry is FileInfo && string.Equals(entry.Extension, ".nfo", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => Path.GetFileNameWithoutExtension(entry.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().LastWriteTimeUtc, StringComparer.Ordinal);

        // A recycle bin is Wondarr's own storage; its marker is what identifies one.
        if (entries.Exists(entry => string.Equals(entry.Name, RecycleBin.MarkerFileName, StringComparison.Ordinal)))
        {
            return new Listing([], []);
        }

        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            // Hidden entries cover Wondarr's own temporary folders, Mac resource forks ("._name.mkv") and Synology's metadata.
            if (entry.Name.StartsWith('.') || IgnoredFolders.Contains(entry.Name) || (entry.Attributes & FileAttributes.Hidden) != 0)
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                // A symlinked folder or junction can point anywhere, including back at an ancestor.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || IsSame(entry.FullName, recycleBin))
                {
                    continue;
                }

                directories.Add(entry.FullName);
            }
            else if (VideoExtensions.Contains(entry.Extension) && entry is FileInfo file)
            {
                // A symlinked file can point anywhere, off the share included: it is not the folder's own video.
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
                {
                    continue;
                }

                DateTime? nfoTime = nfoTimes.TryGetValue(Path.GetFileNameWithoutExtension(file.Name), out var time) ? time : null;
                files.Add(new Found(file.FullName, file.Length, file.LastWriteTimeUtc, nfoTime));
            }
        }

        return new Listing(files, directories);
    }

    /// <summary>Whether a row's file lies inside the root the scan is about.</summary>
    private static bool InRoot(string root, string path)
    {
        try
        {
            return PathRules.IsStrictlyInside(root, path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSame(string first, string second)
    {
        try
        {
            return PathRules.AreEqual(first, second);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string RelativeKey(string root, string path) =>
        ReferenceScanner.RelativeKey(Path.GetRelativePath(root, path));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Missing} of {Rows} music video files are missing; this looks like a share that is not mounted, so none is released")]
    private static partial void LogMountProblem(ILogger logger, int missing, int rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "The video file {Path} stayed missing; its row is let go (song {SongId})")]
    private static partial void LogReleased(ILogger logger, string path, long songId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a folder of the video root that could not be listed.")]
    private static partial void LogFolderSkipped(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Scanned the video folder: {Seen} videos, {Added} added, {Changed} changed, {Missing} missing")]
    private static partial void LogScanned(ILogger logger, int seen, int added, int changed, int missing);

    private sealed record Found(string Path, long Size, DateTime ModifiedAt, DateTime? NfoModifiedAt);

    private sealed record Work(Found File, bool Probe);

    private sealed record Listing(List<Found> Files, List<string> Directories);

    private sealed record Walked(List<Found> Files, List<string> Skipped);

    private sealed record ReadResult(VideoInfo? Probe, MusicVideoNfoData? Nfo);
}
