using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Organizer;

/// <summary>
/// Where replaced files go. An upgrade never deletes the file it replaces: it moves it here, under
/// the library-relative path it had, until the cleanup age passes.
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// Moves <paramref name="path"/> into the recycle bin and returns its new path.
    /// </summary>
    /// <param name="path">The file to recycle.</param>
    /// <param name="libraryRoot">The library the file belongs to, which decides its layout in the bin.</param>
    /// <param name="cancellationToken">Cancels between steps.</param>
    Task<string> RecycleAsync(string path, string libraryRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes recycled files older than the configured age, then the directories they leave empty.
    /// Returns how many files went.
    /// </summary>
    Task<int> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Written fresh for Wondarr (the ordering and clash behaviour are the same as Lidarr's
/// <c>RecycleBinProvider</c>, but nothing here is copied: it uses <see cref="IDiskOperations"/>,
/// <see cref="TimeProvider"/> and the import options instead of Lidarr's providers).
/// </summary>
public sealed class RecycleBin : IRecycleBin
{
    /// <summary>How many clash suffixes to try before giving up. Lidarr stops at the same place.</summary>
    private const int MaxClashSuffix = 99;

    /// <summary>
    /// The empty file that marks a directory as one of our recycle bins. The cleanup pass deletes
    /// files, so it refuses to touch a directory that does not carry this: a misconfigured
    /// <c>import.recycle_bin_path</c> pointing at a library — or at anything else Wondarr did not
    /// write — costs nothing.
    /// </summary>
    public const string MarkerFileName = ".wondarr-recycle-bin";

    private readonly IDiskOperations _disk;
    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly WondarrPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecycleBin> _logger;
    private readonly IServiceScopeFactory? _scopes;

    /// <summary>
    /// Creates the recycle bin over the resolved import options. <paramref name="scopes"/> lets the
    /// cleanup pass read the library and reference-library roots it must never walk; without it
    /// (a bare unit test) no root is known and only the marker guards the pass.
    /// </summary>
    public RecycleBin(
        IDiskOperations disk,
        IOptionsMonitor<ImportOptions> options,
        WondarrPaths paths,
        TimeProvider timeProvider,
        ILogger<RecycleBin> logger,
        IServiceScopeFactory? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _disk = disk;
        _options = options;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
        _scopes = scopes;
    }

    /// <summary>The recycle bin directory in use, re-read so a settings change is picked up.</summary>
    public string Root => _options.CurrentValue.ResolveRecycleBinPath(_paths);

    /// <inheritdoc />
    public Task<string> RecycleAsync(string path, string libraryRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Root;

        // A bin inside the library — or holding it — would recycle a file into the tree it is being
        // moved within, and the cleanup pass would walk the library. Refuse outright.
        if (!string.IsNullOrWhiteSpace(libraryRoot)
            && (PathRules.IsInside(root, libraryRoot) || PathRules.IsInside(libraryRoot, root)))
        {
            throw new InvalidOperationException("The recycle bin must be outside the library");
        }

        var destination = Path.Combine(root, RelativeName(path, libraryRoot));

        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"Cannot work out where to recycle '{path}'.");
        }

        _disk.CreateDirectory(directory);
        var recycled = UniqueDestination(destination);

        // The bin marks itself as ours before the first file lands in it, so a bin that has never
        // been written to is never walked and never cleaned. The marker goes down last of all the
        // steps that can fail before the move: a placement that stops here leaves no marker behind
        // in a directory that holds nothing.
        MarkAsRecycleBin(root);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _disk.MoveFile(path, recycled);
        }
        catch (IOException)
        {
            // A different device under the recycle bin: copy it over, and only then drop the source.
            _disk.CopyFile(path, recycled);

            // A copy that is not the size of the file it came from is not that file: keep the
            // original and let the caller hear about it.
            if (_disk.GetFileSize(recycled) != _disk.GetFileSize(path))
            {
                _disk.DeleteFile(recycled);

                throw new IOException(
                    $"Recycled '{path}' as '{recycled}' with the wrong size; the original was kept.");
            }

            _disk.DeleteFile(path);
        }

        // From here the cleanup clock runs, not from when the file was last downloaded (Lidarr does
        // the same): a file recycled from a decade-old collection is not due for deletion today.
        // The file has already moved, so a stamp that fails (a file owned by another uid, or a mount
        // that refuses utimensat) must not turn the recycle into a failure: the caller would report
        // the old file as untouched while it sits in the bin. Stamping before the move would not
        // help the cross-device copy, whose copy gets its own time anyway, so the stamp stays here
        // and is best-effort.
        try
        {
            try
            {
                _disk.SetLastWriteTimeUtc(recycled, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RecycleBinLog.StampFailed(_logger, recycled, exception.Message);
            }

            RecycleBinLog.Recycled(_logger, path, recycled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything that goes wrong now happens to a file that is already in the bin: the caller
            // must be told where it is, not left to think the old file is untouched.
            throw new RecycleMovedException(recycled, exception);
        }

        return Task.FromResult(recycled);
    }

    /// <inheritdoc />
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var days = _options.CurrentValue.RecycleBinCleanupDays;

        if (days <= 0)
        {
            return 0;
        }

        var root = Root;

        if (!_disk.DirectoryExists(root))
        {
            return 0;
        }

        // Not a bin we wrote: leave every file in it alone. This is the guard that makes a stray
        // import.recycle_bin_path harmless rather than destructive.
        if (!_disk.FileExists(Path.Combine(root, MarkerFileName)))
        {
            RecycleBinLog.NotAMarkedBin(_logger, root);
            return 0;
        }

        var protectedRoots = await LoadProtectedRootsAsync(cancellationToken).ConfigureAwait(false);

        // A bin that sits inside a library or reference root is somebody's collection, not recycled
        // files: a marker written there by an old configuration does not make it ours.
        var holder = protectedRoots.FirstOrDefault(libraryRoot => PathRules.IsInside(libraryRoot, root));

        if (holder is not null)
        {
            RecycleBinLog.BinInsideLibrary(_logger, root, holder);
            return 0;
        }

        // A root inside the bin (or the bin holding one) is skipped directory by directory.
        var skipped = protectedRoots.Where(libraryRoot => PathRules.IsInside(root, libraryRoot)).ToList();

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var deleted = 0;
        var directories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in _disk.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (skipped.Any(libraryRoot => PathRules.IsInside(libraryRoot, file)))
            {
                continue;
            }

            // Belt and braces: only a file strictly below the bin is ever deleted from here — never
            // the bin root itself, never its marker, and never anything the walk was led to outside
            // the bin.
            if (!PathRules.IsStrictlyInside(root, file)
                || string.Equals(Path.GetFileName(file), MarkerFileName, StringComparison.Ordinal)
                || !_disk.FileExists(file))
            {
                continue;
            }

            if (_disk.GetLastWriteTimeUtc(file) >= cutoff)
            {
                continue;
            }

            try
            {
                _disk.DeleteFile(file);
            }
            catch (IOException exception)
            {
                // One file that is locked or already gone does not end the pass.
                RecycleBinLog.DeleteFailed(_logger, file, exception.Message);
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                RecycleBinLog.DeleteFailed(_logger, file, exception.Message);
                continue;
            }

            deleted++;

            // The file's folder and every folder between it and the bin, the bin root excluded: the
            // ones that only held expired files should go with them.
            for (var parent = Path.GetDirectoryName(file);
                 !string.IsNullOrEmpty(parent) && PathRules.IsStrictlyInside(root, parent);
                 parent = Path.GetDirectoryName(parent))
            {
                directories.Add(parent);
            }
        }

        // Deepest first, so a directory that only held an expired file goes too.
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _disk.DeleteEmptyDirectory(directory);
        }

        if (deleted > 0)
        {
            RecycleBinLog.Cleaned(_logger, deleted, days);
        }

        return deleted;
    }

    /// <summary>
    /// The roots of every library and reference library, as full paths. Empty when the bin was built
    /// without a scope factory.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadProtectedRootsAsync(CancellationToken cancellationToken)
    {
        if (_scopes is null)
        {
            return [];
        }

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            var libraries = await database.Libraries
                .AsNoTracking()
                .Select(library => library.RootPath)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var references = await database.ReferenceLibraries
                .AsNoTracking()
                .Select(library => library.RootPath)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var roots = new List<string>();

            foreach (var path in libraries.Concat(references))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    roots.Add(Path.GetFullPath(path));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Not a path at all: it cannot be inside the bin.
                }
            }

            return roots;
        }
    }

    /// <summary>
    /// The file's place in the bin: its path relative to the library, or its name when it came from
    /// outside the library.
    /// </summary>
    private static string RelativeName(string path, string libraryRoot)
    {
        var full = Path.GetFullPath(path);

        if (!string.IsNullOrWhiteSpace(libraryRoot) && PathRules.IsInside(libraryRoot, full))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(libraryRoot), full);

            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                return relative;
            }
        }

        return Path.GetFileName(full);
    }

    /// <summary>Stamps the bin root as ours. An existing marker — and its age — is left alone.</summary>
    private void MarkAsRecycleBin(string root)
    {
        try
        {
            _disk.CreateDirectory(root);
            _disk.CreateEmptyFile(Path.Combine(root, MarkerFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot write the recycle bin marker in '{root}': {exception.Message}", exception);
        }
    }

    /// <summary>Appends <c> (1)</c>, <c> (2)</c> … before the extension until the name is free.</summary>
    private string UniqueDestination(string destination)
    {
        if (!_disk.FileExists(destination) && !_disk.DirectoryExists(destination))
        {
            return destination;
        }

        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);

        for (var suffix = 1; suffix <= MaxClashSuffix; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");

            if (!_disk.FileExists(candidate) && !_disk.DirectoryExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Cannot recycle to '{destination}': every name up to ({MaxClashSuffix}) is taken.");
    }
}

/// <summary>
/// Thrown when something fails after a file has already been moved into the recycle bin. It carries
/// where the file is, so the placer can put it back, or report the path so a caller can record it.
/// </summary>
public sealed class RecycleMovedException : IOException
{
    /// <summary>Initialises a new instance of the <see cref="RecycleMovedException"/> class.</summary>
    /// <param name="recycledPath">Where the file now is.</param>
    /// <param name="innerException">What failed after the move.</param>
    public RecycleMovedException(string recycledPath, Exception innerException)
        : base($"The file was moved to '{recycledPath}', but a later step failed: {innerException?.Message}", innerException)
    {
        RecycledPath = recycledPath;
    }

    /// <summary>Gets where the file is in the bin.</summary>
    public string RecycledPath { get; }
}

/// <summary>What the recycle bin writes to the log. Source-generated, so a disabled log costs nothing.</summary>
internal static partial class RecycleBinLog
{
    /// <summary>A file was replaced and parked.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Recycled {Source} to {Target}")]
    internal static partial void Recycled(ILogger logger, string source, string target);

    /// <summary>The cleanup pass expired some recycled files.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} recycled files older than {Days} days")]
    internal static partial void Cleaned(ILogger logger, int count, int days);

    /// <summary>One expired file could not be removed; the pass carried on with the rest.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the recycled file {Path}: {Reason}")]
    internal static partial void DeleteFailed(ILogger logger, string path, string reason);

    /// <summary>Stamping the cleanup clock on a recycled file failed; the file is in the bin regardless.</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Recycled {Path} but could not set its time: {Reason}. Cleanup will use the file's own time, which may be old")]
    internal static partial void StampFailed(ILogger logger, string path, string reason);

    /// <summary>The configured bin sits inside a library or reference root, so cleanup did nothing.</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The recycle bin {Path} is inside the library root {Root}, so cleanup left it untouched")]
    internal static partial void BinInsideLibrary(ILogger logger, string path, string root);

    /// <summary>The configured directory is not a bin Wondarr wrote, so nothing in it was touched.</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The recycle bin {Path} carries no marker, so cleanup left it untouched")]
    internal static partial void NotAMarkedBin(ILogger logger, string path);
}
