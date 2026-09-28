using Wondarr.Core.Configuration;
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

    private readonly IDiskOperations _disk;
    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly WondarrPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecycleBin> _logger;

    /// <summary>Creates the recycle bin over the resolved import options.</summary>
    public RecycleBin(
        IDiskOperations disk,
        IOptionsMonitor<ImportOptions> options,
        WondarrPaths paths,
        TimeProvider timeProvider,
        ILogger<RecycleBin> logger)
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
        _disk.SetLastWriteTimeUtc(recycled, _timeProvider.GetUtcNow().UtcDateTime);

        RecycleBinLog.Recycled(_logger, path, recycled);

        return Task.FromResult(recycled);
    }

    /// <inheritdoc />
    public Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var days = _options.CurrentValue.RecycleBinCleanupDays;

        if (days <= 0)
        {
            return Task.FromResult(0);
        }

        var root = Root;

        if (!_disk.DirectoryExists(root))
        {
            return Task.FromResult(0);
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var deleted = 0;
        var directories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in _disk.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Belt and braces: only a file strictly below the bin is ever deleted from here — never
            // the bin root itself, and never anything the walk was led to outside it.
            if (!PathRules.IsStrictlyInside(root, file) || !_disk.FileExists(file))
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

        return Task.FromResult(deleted);
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
}
