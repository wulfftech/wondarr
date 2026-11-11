using System.Text;
using Wondarr.Core.Domain;

namespace Wondarr.Core.Organizer;

/// <summary>Whether a library's root folder is safe to write into.</summary>
public enum LibraryRootStatus
{
    /// <summary>The folder exists and holds the Wondarr marker file.</summary>
    Ready,

    /// <summary>The folder does not exist.</summary>
    Missing,

    /// <summary>The folder exists but holds no marker, so it may be an unmounted share's empty mount point.</summary>
    Unmarked,

    /// <summary>The disk did not answer in time, so the share may be hung. Only <see cref="ILibraryRoots.CheckAsync"/> returns it.</summary>
    Unresponsive,
}

/// <summary>
/// Tells whether a library root is the folder the user chose or only an empty mount point. Every
/// library root holds a marker file; Wondarr writes into a library only while it is there.
/// </summary>
public interface ILibraryRoots
{
    /// <summary>Checks the root on the disk, live and uncached.</summary>
    /// <param name="rootPath">The library's root folder.</param>
    LibraryRootStatus Check(string rootPath);

    /// <summary>
    /// <see cref="Check"/> on the thread pool, bounded at <see cref="LibraryRoots.CheckTimeout"/>: a hung
    /// mount answers <see cref="LibraryRootStatus.Unresponsive"/> instead of holding the caller.
    /// </summary>
    /// <param name="rootPath">The library's root folder.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task<LibraryRootStatus> CheckAsync(string rootPath, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the marker into a folder that already exists; does nothing and returns <see langword="false"/>
    /// when it does not. Never creates the root.
    /// </summary>
    /// <param name="rootPath">The library's root folder.</param>
    bool MarkExisting(string rootPath);

    /// <summary>Creates the folder when needed and writes the marker; an existing marker is left as it is.</summary>
    /// <param name="rootPath">The library's root folder.</param>
    void Mark(string rootPath);

    /// <summary>The user-facing sentence for a root that is not ready.</summary>
    /// <param name="library">The library.</param>
    /// <param name="status">The status <see cref="Check"/> returned.</param>
    string Describe(Library library, LibraryRootStatus status);
}

/// <summary>The one implementation of <see cref="ILibraryRoots"/>.</summary>
public sealed class LibraryRoots : ILibraryRoots
{
    /// <summary>The name of the marker file at the top of a library root.</summary>
    public const string MarkerFileName = ".wondarr-library";

    /// <summary>What the marker says; it is never parsed, its existence is the test.</summary>
    internal const string MarkerText =
        "This folder is a Wondarr library. Wondarr writes into it only while this file is here, so an unmounted share is never filled. Delete it only when this folder is no longer a Wondarr library.";

    /// <summary>How long <see cref="CheckAsync"/> waits for the disk.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IDiskOperations _disk;
    private readonly TimeSpan _timeout;

    /// <summary>Initialises a new instance of the <see cref="LibraryRoots"/> class.</summary>
    /// <param name="disk">The disk seam.</param>
    /// <param name="timeout">How long <see cref="CheckAsync"/> waits; <see cref="CheckTimeout"/> when <see langword="null"/>.</param>
    public LibraryRoots(IDiskOperations disk, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(disk);

        _disk = disk;
        _timeout = timeout ?? CheckTimeout;
    }

    /// <inheritdoc />
    public async Task<LibraryRootStatus> CheckAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Check(rootPath), CancellationToken.None)
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return LibraryRootStatus.Unresponsive;
        }
    }

    /// <inheritdoc />
    public bool MarkExisting(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !_disk.DirectoryExists(rootPath))
        {
            return false;
        }

        _disk.WriteNewFile(Path.Combine(rootPath, MarkerFileName), Utf8NoBom.GetBytes(MarkerText));

        return true;
    }

    /// <inheritdoc />
    public LibraryRootStatus Check(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !_disk.DirectoryExists(rootPath))
        {
            return LibraryRootStatus.Missing;
        }

        return _disk.FileExists(Path.Combine(rootPath, MarkerFileName))
            ? LibraryRootStatus.Ready
            : LibraryRootStatus.Unmarked;
    }

    /// <inheritdoc />
    public void Mark(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _disk.CreateDirectory(rootPath);
        _disk.WriteNewFile(Path.Combine(rootPath, MarkerFileName), Utf8NoBom.GetBytes(MarkerText));
    }

    /// <inheritdoc />
    public string Describe(Library library, LibraryRootStatus status)
    {
        ArgumentNullException.ThrowIfNull(library);

        return status switch
        {
            LibraryRootStatus.Missing =>
                $"Library \"{library.Name}\": {library.RootPath} does not exist. Imports into it wait until it is back; if the share is unmounted, mount it.",
            LibraryRootStatus.Unmarked =>
                $"Library \"{library.Name}\": {library.RootPath} has no {MarkerFileName} file, so it looks like an unmounted share. Imports into it wait. If this is the right folder, confirm it under Settings → Library.",
            LibraryRootStatus.Unresponsive =>
                $"Library \"{library.Name}\": {library.RootPath} did not answer within 5 seconds, so its share may be hung. Imports into it wait until it answers.",
            _ => $"Library \"{library.Name}\": {library.RootPath} is ready.",
        };
    }
}
