using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

namespace Wondarr.Core.FileSystem;

/// <summary>A folder in a listing.</summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Path">The folder's full path.</param>
public sealed record FolderEntry(string Name, string Path);

/// <summary>A place the picker may enter.</summary>
/// <param name="Path">The root's full path.</param>
/// <param name="Unavailable">Whether the root did not answer within five seconds, so its share may be hung.</param>
public sealed record RootEntry(string Path, bool Unavailable = false);

/// <summary>What the folder picker shows.</summary>
/// <param name="Path">The folder listed, or <see langword="null"/> for the list of roots alone.</param>
/// <param name="Parent">The folder above, or <see langword="null"/> at a root.</param>
/// <param name="Folders">The sub-folders, never files, without hidden ones or links.</param>
/// <param name="Roots">The places the picker may enter: mounted volumes in a container, else the file system roots.</param>
public sealed record FolderListing(
    string? Path,
    string? Parent,
    IReadOnlyList<FolderEntry> Folders,
    IReadOnlyList<RootEntry> Roots);

/// <summary>How a browse or create call ended.</summary>
public enum FileSystemOutcome
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The path or name is not allowed: relative, with <c>..</c>, outside every root, through a link, a system folder, or not a plain name.</summary>
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
/// A path is judged by its text and by the links on its way: relative paths and <c>..</c> are refused, the folder has
/// to sit inside one of the roots, no component under the root may be a link, and neither Wondarr's data directory,
/// the recycle bin nor (outside a container) a system folder can be entered. Every disk call waits at most
/// <see cref="LibraryRoots.CheckTimeout"/>; a root that does not answer is listed as unavailable rather than holding
/// the others up, and a folder that does not answer is <see cref="FileSystemOutcome.TimedOut"/>.
/// </remarks>
public sealed partial class FileSystemBrowser : IFileSystemBrowser
{
    private const int MaxNameLength = 255;

    /// <summary>
    /// The system folders the picker never enters outside a container, in one place. Under a container the roots are
    /// the mounts and nothing else is reachable.
    /// </summary>
    private static readonly string[] LinuxSystemFolders =
    [
        "/proc", "/sys", "/dev", "/etc", "/boot", "/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/var/lib", "/run",
    ];

    private readonly ContainerMounts _containers;
    private readonly WondarrPaths _paths;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;

    /// <summary>Initialises a new instance of the <see cref="FileSystemBrowser"/> class.</summary>
    /// <param name="containers">Tells whether Wondarr is in a container, and what is mounted.</param>
    /// <param name="paths">Where Wondarr's data directory is, which is never entered.</param>
    /// <param name="importOptions">Where the recycle bin is, which is never entered.</param>
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
        var roots = await ReadRootsAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new(FileSystemOutcome.Ok, new FolderListing(null, null, [], roots));
        }

        var bin = _importOptions.CurrentValue.ResolveRecycleBinPath(_paths);

        try
        {
            return await MusicVideoDisk.RunAsync(
                () =>
                {
                    if (Resolve(path, roots, bin) is { } refusal)
                    {
                        return new FileSystemResult<FolderListing>(FileSystemOutcome.Invalid, null, refusal.Message);
                    }

                    var full = FullPath(path);
                    var directory = new DirectoryInfo(full);

                    if (!directory.Exists)
                    {
                        return new FileSystemResult<FolderListing>(FileSystemOutcome.NotFound, null, "That folder does not exist.");
                    }

                    var folders = directory.EnumerateDirectories()
                        .Where(child => IsListable(child, bin))
                        .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(child => new FolderEntry(child.Name, child.FullName))
                        .ToList();

                    var parent = Path.GetDirectoryName(full);
                    parent = parent is not null && roots.Any(root => PathRules.IsInside(root.Path, parent)) ? parent : null;

                    return new FileSystemResult<FolderListing>(FileSystemOutcome.Ok, new FolderListing(full, parent, folders, roots));
                },
                cancellationToken).ConfigureAwait(false);
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

        var roots = await ReadRootsAsync(cancellationToken).ConfigureAwait(false);
        var bin = _importOptions.CurrentValue.ResolveRecycleBinPath(_paths);
        var folderName = name.Trim();

        try
        {
            return await MusicVideoDisk.RunAsync(
                () =>
                {
                    if (Resolve(parent, roots, bin) is { } refusal)
                    {
                        return new FileSystemResult<FolderEntry>(FileSystemOutcome.Invalid, null, refusal.Message);
                    }

                    var full = FullPath(parent);
                    var target = Path.Combine(full, folderName);

                    if (!Directory.Exists(full))
                    {
                        return new FileSystemResult<FolderEntry>(FileSystemOutcome.NotFound, null, "The parent folder does not exist.");
                    }

                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        return new FileSystemResult<FolderEntry>(FileSystemOutcome.Conflict, null, "A folder or file with that name is already there.");
                    }

                    Directory.CreateDirectory(target);

                    return new FileSystemResult<FolderEntry>(FileSystemOutcome.Ok, new FolderEntry(folderName, target));
                },
                cancellationToken).ConfigureAwait(false);
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

        if (trimmed.EndsWith('.'))
        {
            return "name must not end with a dot.";
        }

        if (trimmed.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0)
        {
            return "name must be one folder name, without a path separator or a character a file name cannot have.";
        }

        if (ReservedName().IsMatch(trimmed))
        {
            return "name is reserved by Windows (CON, PRN, AUX, NUL, COM1-9, LPT1-9); choose another.";
        }

        return null;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    private static string FullPath(string path)
    {
        var full = Path.GetFullPath(path.Trim());

        // Keep a root's own separator ("/", "C:\"): trimming it would leave an empty or drive-relative path.
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal)
            ? full
            : Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The system folders of this machine; empty under a container, where the mounts are the only roots.</summary>
    private IEnumerable<string> SystemFolders()
    {
        if (_containers.InContainer)
        {
            return [];
        }

        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }.Where(folder => folder.Length > 0);
        }

        return LinuxSystemFolders;
    }

    private bool IsForbidden(string full, string bin) =>
        PathRules.IsInside(_paths.ConfigDir, full)
        || PathRules.IsInside(bin, full)
        || SystemFolders().Any(folder => PathRules.IsInside(folder, full));

    private bool IsListable(DirectoryInfo child, string bin) =>
        !child.Name.StartsWith('.')
        && !child.Attributes.HasFlag(FileAttributes.Hidden)
        && !IsLink(child)
        && !IsForbidden(child.FullName, bin);

    /// <summary>
    /// Why <paramref name="path"/> may not be entered, or <see langword="null"/> when it may: it has to be absolute,
    /// free of <c>..</c>, inside a root, not forbidden, and reached without a link. Reads the disk to look for links.
    /// </summary>
    private Refusal? Resolve(string path, IReadOnlyList<RootEntry> roots, string bin)
    {
        var trimmed = path.Trim();

        if (!Path.IsPathFullyQualified(trimmed) || trimmed.Split('/', '\\').Contains(".."))
        {
            return new Refusal("path must be an absolute folder inside one of the roots, without '..'.");
        }

        string full;

        try
        {
            full = FullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Refusal("path is not a usable path.");
        }

        var root = roots
            .Where(candidate => PathRules.IsInside(candidate.Path, full))
            .OrderByDescending(candidate => candidate.Path.Length)
            .FirstOrDefault();

        if (root is null)
        {
            return new Refusal("path must be an absolute folder inside one of the roots, without '..'.");
        }

        if (IsForbidden(full, bin))
        {
            return new Refusal("That folder cannot be used: it is Wondarr's own, the recycle bin or a system folder.");
        }

        // A link on the way could lead out of the mount, so no component under the root may be one.
        var current = root.Path;

        foreach (var segment in full[root.Path.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);

            if (!info.Exists)
            {
                break;
            }

            if (IsLink(info))
            {
                return new Refusal("path goes through a link, which could lead out of the mounted folder.");
            }
        }

        return null;
    }

    /// <summary>The roots, each probed on its own with its own time limit, so one hung share costs only itself.</summary>
    private async Task<IReadOnlyList<RootEntry>> ReadRootsAsync(CancellationToken cancellationToken)
    {
        IEnumerable<string> candidates;
        Func<string, bool> probe;

        if (_containers.InContainer)
        {
            candidates = _containers.DataMountPoints().Where(mount => ProtectedPaths.Reason(mount, _paths) is null);
            probe = _containers.IsDirectory;
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates = DriveInfo.GetDrives()
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(drive => drive.Name);
            probe = name => new DriveInfo(name).IsReady;
        }
        else
        {
            return [new RootEntry("/")];
        }

        var probes = candidates.Select(async candidate =>
        {
            try
            {
                return await MusicVideoDisk.RunAsync(() => probe(candidate), cancellationToken).ConfigureAwait(false)
                    ? new RootEntry(candidate)
                    : null;
            }
            catch (MusicVideoTimeoutException)
            {
                return new RootEntry(candidate, Unavailable: true);
            }
        }).ToList();

        return [.. (await Task.WhenAll(probes).ConfigureAwait(false)).OfType<RootEntry>().OrderBy(root => root.Path, StringComparer.Ordinal)];
    }

    private sealed record Refusal(string Message);
}
