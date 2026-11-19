using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

namespace Wondarr.Core.FileSystem;

/// <summary>A folder in a listing.</summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Path">The folder's full path.</param>
public sealed record FolderEntry(string Name, string Path);

/// <summary>What the folder picker shows.</summary>
/// <param name="Path">The folder listed, or <see langword="null"/> for the list of roots alone.</param>
/// <param name="Parent">The folder above, or <see langword="null"/> at a root.</param>
/// <param name="Folders">The sub-folders, never files, without hidden ones.</param>
/// <param name="Roots">The places the picker may enter: mounted volumes in a container, else the file system roots.</param>
public sealed record FolderListing(
    string? Path,
    string? Parent,
    IReadOnlyList<FolderEntry> Folders,
    IReadOnlyList<string> Roots);

/// <summary>How a browse or create call ended.</summary>
public enum FileSystemOutcome
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The path or name is not allowed: relative, with <c>..</c>, outside every root, or not a plain name.</summary>
    Invalid,

    /// <summary>The folder does not exist.</summary>
    NotFound,

    /// <summary>The folder to create is already there.</summary>
    Conflict,

    /// <summary>The disk did not answer within five seconds.</summary>
    TimedOut,
}

/// <summary>The outcome of a browse or create call.</summary>
/// <typeparam name="T">What a success carries.</typeparam>
/// <param name="Outcome">How it ended.</param>
/// <param name="Value">The result on <see cref="FileSystemOutcome.Ok"/>.</param>
/// <param name="Message">Why it was refused, otherwise <see langword="null"/>.</param>
public sealed record FileSystemResult<T>(FileSystemOutcome Outcome, T? Value, string? Message = null)
    where T : class;

/// <summary>Lists and creates folders, only where a folder may be chosen.</summary>
public interface IFileSystemBrowser
{
    /// <summary>The roots alone when <paramref name="path"/> is <see langword="null"/>, else that folder's sub-folders.</summary>
    /// <param name="path">An absolute folder inside a root, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<FileSystemResult<FolderListing>> BrowseAsync(string? path, CancellationToken cancellationToken);

    /// <summary>Creates a folder named <paramref name="name"/> inside <paramref name="parent"/>.</summary>
    /// <param name="parent">An existing absolute folder inside a root.</param>
    /// <param name="name">One plain path segment.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<FileSystemResult<FolderEntry>> CreateFolderAsync(string parent, string name, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// A path is judged by its text, never followed out of a root: relative paths and <c>..</c> are refused, and the
/// folder has to sit inside one of the roots. Every disk call waits at most
/// <see cref="LibraryRoots.CheckTimeout"/>, so a hung share answers
/// <see cref="FileSystemOutcome.TimedOut"/> instead of holding the request.
/// </remarks>
public sealed class FileSystemBrowser : IFileSystemBrowser
{
    private const int MaxNameLength = 255;

    private readonly ContainerMounts _containers;
    private readonly WondarrPaths _paths;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;

    /// <summary>Initialises a new instance of the <see cref="FileSystemBrowser"/> class.</summary>
    /// <param name="containers">Tells whether Wondarr is in a container, and what is mounted.</param>
    /// <param name="paths">Where Wondarr's data directory is, which is never a root.</param>
    /// <param name="importOptions">Where the recycle bin is, which is never listed.</param>
    public FileSystemBrowser(ContainerMounts containers, WondarrPaths paths, IOptionsMonitor<ImportOptions> importOptions)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(importOptions);

        _containers = containers;
        _paths = paths;
        _importOptions = importOptions;
    }

    /// <inheritdoc />
    public async Task<FileSystemResult<FolderListing>> BrowseAsync(string? path, CancellationToken cancellationToken)
    {
        try
        {
            var roots = await MusicVideoDisk.RunAsync(ReadRoots, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(path))
            {
                return new(FileSystemOutcome.Ok, new FolderListing(null, null, [], roots));
            }

            if (Resolve(path, roots) is not { } full)
            {
                return new(FileSystemOutcome.Invalid, null, "path must be an absolute folder inside one of the roots, without '..'.");
            }

            var bin = _importOptions.CurrentValue.ResolveRecycleBinPath(_paths);

            var folders = await MusicVideoDisk.RunAsync(() => List(full, bin), cancellationToken).ConfigureAwait(false);

            if (folders is null)
            {
                return new(FileSystemOutcome.NotFound, null, "That folder does not exist.");
            }

            var parent = Path.GetDirectoryName(full);
            parent = parent is not null && roots.Any(root => PathRules.IsInside(root, parent)) ? parent : null;

            return new(FileSystemOutcome.Ok, new FolderListing(full, parent, folders, roots));
        }
        catch (MusicVideoTimeoutException)
        {
            return new(FileSystemOutcome.TimedOut, null, "The folder did not respond, so its share may be hung.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(FileSystemOutcome.NotFound, null, $"That folder cannot be read: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FileSystemResult<FolderEntry>> CreateFolderAsync(string parent, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);

        if (NameProblem(name) is { } problem)
        {
            return new(FileSystemOutcome.Invalid, null, problem);
        }

        try
        {
            var roots = await MusicVideoDisk.RunAsync(ReadRoots, cancellationToken).ConfigureAwait(false);

            if (Resolve(parent, roots) is not { } full)
            {
                return new(FileSystemOutcome.Invalid, null, "parent must be an absolute folder inside one of the roots, without '..'.");
            }

            var folderName = name.Trim();
            var target = Path.Combine(full, folderName);

            var outcome = await MusicVideoDisk.RunAsync(
                () =>
                {
                    if (!Directory.Exists(full))
                    {
                        return FileSystemOutcome.NotFound;
                    }

                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        return FileSystemOutcome.Conflict;
                    }

                    Directory.CreateDirectory(target);

                    return FileSystemOutcome.Ok;
                },
                cancellationToken).ConfigureAwait(false);

            return outcome switch
            {
                FileSystemOutcome.Ok => new(FileSystemOutcome.Ok, new FolderEntry(folderName, target)),
                FileSystemOutcome.NotFound => new(FileSystemOutcome.NotFound, null, "The parent folder does not exist."),
                _ => new(outcome, null, "A folder or file with that name is already there."),
            };
        }
        catch (MusicVideoTimeoutException)
        {
            return new(FileSystemOutcome.TimedOut, null, "The folder did not respond, so its share may be hung.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(FileSystemOutcome.Invalid, null, $"The folder could not be created: {exception.Message}");
        }
    }

    /// <summary>The problem with a folder name, or <see langword="null"/> when it is one plain segment.</summary>
    internal static string? NameProblem(string name)
    {
        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            return "name must not be empty.";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"name must be at most {MaxNameLength} characters.";
        }

        if (trimmed.StartsWith('.'))
        {
            return "name must not start with a dot.";
        }

        if (trimmed.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0)
        {
            return "name must be one folder name, without a path separator or a character a file name cannot have.";
        }

        return null;
    }

    private List<string> ReadRoots()
    {
        if (_containers.InContainer)
        {
            return [.. _containers.DataMounts().Where(mount => ProtectedPaths.Reason(mount, _paths) is null)];
        }

        if (OperatingSystem.IsWindows())
        {
            return
            [
                .. DriveInfo.GetDrives()
                    .Where(drive => (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network) && drive.IsReady)
                    .Select(drive => drive.RootDirectory.FullName),
            ];
        }

        return ["/"];
    }

    /// <summary>The full path when it is absolute, free of <c>..</c> and inside a root; otherwise <see langword="null"/>.</summary>
    private static string? Resolve(string path, IReadOnlyList<string> roots)
    {
        var trimmed = path.Trim();

        if (!Path.IsPathFullyQualified(trimmed) || trimmed.Split('/', '\\').Contains(".."))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(trimmed);

            // Keep a root's own separator ("/", "C:\"): trimming it would leave an empty or drive-relative path.
            if (!string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
            {
                full = Path.TrimEndingDirectorySeparator(full);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return roots.Any(root => PathRules.IsInside(root, full)) ? full : null;
    }

    private static List<FolderEntry>? List(string full, string bin)
    {
        var directory = new DirectoryInfo(full);

        if (!directory.Exists)
        {
            return null;
        }

        return
        [
            .. directory.EnumerateDirectories()
                .Where(child => !child.Name.StartsWith('.')
                    && !child.Attributes.HasFlag(FileAttributes.Hidden)
                    && !PathRules.AreEqual(child.FullName, bin))
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => new FolderEntry(child.Name, child.FullName)),
        ];
    }
}
