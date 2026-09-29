using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Organizer;

/// <summary>How a file gets from the download to the library.</summary>
public enum TransferMode
{
    /// <summary>The download is consumed: Soulseek's temporary copy has no other owner.</summary>
    Move,

    /// <summary>The source stays where it is and the library gets its own copy.</summary>
    Copy,

    /// <summary>The source stays, and the library gets a hard link to the same blocks when it can.</summary>
    HardLinkOrCopy,
}

/// <summary>One file to place: the download, the library, and the name the policy rendered.</summary>
/// <param name="SourcePath">The verified, tagged file to place.</param>
/// <param name="LibraryRoot">The library root the target path is relative to.</param>
/// <param name="RelativePathWithoutExtension">The target path inside the root, without the extension.</param>
/// <param name="Extension">The extension without its dot, for example <c>mp3</c>.</param>
/// <param name="Mode">How to move the bytes.</param>
/// <param name="ReplacesPath">The file this placement upgrades, recycled first when it exists.</param>
public sealed record PlacementRequest(
    string SourcePath,
    string LibraryRoot,
    string RelativePathWithoutExtension,
    string Extension,
    TransferMode Mode,
    string? ReplacesPath);

/// <summary>What became of a placement. A failure carries whatever the caller still needs to know.</summary>
/// <param name="Success">Whether the file is now at <paramref name="FinalPath"/> as asked.</param>
/// <param name="FinalPath">Where the file ended up, when it reached the library at all.</param>
/// <param name="RecycledPath">Where the replaced file went, when one was recycled and could not go back.</param>
/// <param name="ModeUsed">How the bytes were transferred, when they were.</param>
/// <param name="Error">What went wrong, when something did.</param>
public sealed record PlacementResult(
    bool Success,
    string? FinalPath,
    string? RecycledPath,
    TransferMode? ModeUsed,
    string? Error);

/// <summary>Puts a verified file at its library path without ever losing data.</summary>
public interface IFilePlacer
{
    /// <summary>Places one file and reports where it went.</summary>
    Task<PlacementResult> PlaceAsync(PlacementRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The only code in Wondarr that puts a file into the library. It refuses to place anything outside
/// the library root, never overwrites a file it did not put there, recycles what it replaces — and
/// puts that file back when the placement then fails — and leaves the placed file alone when a later
/// step fails: the caller decides what to do about it.
/// </summary>
public sealed class FilePlacer : IFilePlacer
{
    /// <summary>How many collision suffixes to try before giving up.</summary>
    private const int MaxCollisionSuffix = 99;

    private readonly IDiskOperations _disk;
    private readonly IRecycleBin _recycleBin;
    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly ILogger<FilePlacer> _logger;

    /// <summary>Creates the placer over the disk, the recycle bin and the import options.</summary>
    public FilePlacer(
        IDiskOperations disk,
        IRecycleBin recycleBin,
        IOptionsMonitor<ImportOptions> options,
        ILogger<FilePlacer> logger)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _disk = disk;
        _recycleBin = recycleBin;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlacementResult> PlaceAsync(PlacementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(request.LibraryRoot);
        var target = TargetFor(request);

        // The relative path comes from a naming template and the tags behind it: it never gets to
        // point outside the library, and never at the library root itself.
        if (!PathRules.IsStrictlyInside(root, target))
        {
            return Failed($"'{request.RelativePathWithoutExtension}' resolves outside the library root.");
        }

        if (!_disk.FileExists(request.SourcePath))
        {
            return Failed($"Source file '{request.SourcePath}' does not exist.");
        }

        // Already there: placing the file the library already holds has nothing to do, and nothing
        // to recycle — the file at the target is the source.
        if (_disk.FileExists(target) && _disk.AreSameFile(target, request.SourcePath))
        {
            return new PlacementResult(true, target, null, request.Mode, null);
        }

        var replaces = request.ReplacesPath;
        string? recycled = null;

        if (!string.IsNullOrEmpty(replaces) && _disk.FileExists(replaces))
        {
            // Only a file inside this library, and never the file being placed, may be replaced:
            // anything else would park something the caller never meant to give up.
            if (!PathRules.IsStrictlyInside(root, replaces) || _disk.AreSameFile(replaces, request.SourcePath))
            {
                return Failed($"'{replaces}' is not a file to replace inside the library root.");
            }

            try
            {
                recycled = await _recycleBin
                    .RecycleAsync(replaces, root, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // The old file is untouched, so the source is too: stop before anything moves.
                return Failed($"Could not recycle '{replaces}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return Failed($"Could not recycle '{replaces}': {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                // The bin is not a bin we can use (inside the library, for instance). Nothing has
                // moved, and nothing will: the caller has to fix the configuration first.
                return Failed($"Could not recycle '{replaces}': {exception.Message}");
            }
        }

        // From here the replaced file is out of the way and has to be put back if anything fails, so
        // the token is no longer consulted: a cancelled placement must not leave a hole in the library.

        var resolved = ResolveCollision(target);

        if (resolved is null)
        {
            return Failed(
                $"'{target}' is taken, and so is every name up to ({MaxCollisionSuffix}).",
                RollBack(recycled, replaces));
        }

        target = resolved;

        var directory = Path.GetDirectoryName(target);

        if (string.IsNullOrEmpty(directory))
        {
            return Failed($"'{target}' has no directory to place a file in.", RollBack(recycled, replaces));
        }

        List<string> createdDirectories;

        try
        {
            createdDirectories = EnsureDirectory(directory, root);
        }
        catch (IOException exception)
        {
            return Failed(exception.Message, RollBack(recycled, replaces));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed(exception.Message, RollBack(recycled, replaces));
        }

        TransferMode modeUsed;
        bool hardLinked;

        try
        {
            modeUsed = Transfer(request.SourcePath, target, request.Mode, out hardLinked);
        }
        catch (PlacementTransferException exception)
        {
            // The file reached the library and a later step failed: the target stays, and the caller
            // decides what to do about a placed file whose download was not consumed.
            return new PlacementResult(
                false,
                exception.FinalPath,
                RollBack(recycled, replaces),
                null,
                exception.Message);
        }
        catch (IOException exception)
        {
            return Failed(exception.Message, RollBack(recycled, replaces));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed(exception.Message, RollBack(recycled, replaces));
        }

        FilePlacerLog.Placed(_logger, request.SourcePath, target, modeUsed);

        // The file is in the library now: a failure from here on is reported, not undone. The caller
        // decides whether a wrongly-permissioned file is worse than a missing one.
        try
        {
            ApplyPermissions(target, createdDirectories, hardLinked);
        }
        catch (IOException exception)
        {
            return new PlacementResult(false, target, recycled, modeUsed, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new PlacementResult(false, target, recycled, modeUsed, exception.Message);
        }

        return new PlacementResult(true, target, recycled, modeUsed, null);
    }

    /// <summary>The absolute target: the library root, the rendered relative path and the extension.</summary>
    private static string TargetFor(PlacementRequest request)
    {
        var relative = request.RelativePathWithoutExtension.Replace('/', Path.DirectorySeparatorChar);

        return Path.GetFullPath(Path.Combine(request.LibraryRoot, relative + "." + request.Extension));
    }

    /// <summary>The first free name at or next to <paramref name="target"/>, or null when every suffix is taken.</summary>
    private string? ResolveCollision(string target)
    {
        if (!_disk.FileExists(target))
        {
            return target;
        }

        var directory = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");

            if (!_disk.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates the target directory and returns every directory this placement created, shallowest
    /// first. Nothing above the library root is ever created.
    /// </summary>
    private List<string> EnsureDirectory(string directory, string root)
    {
        var created = new List<string>();
        var current = directory;

        while (!_disk.DirectoryExists(current))
        {
            created.Add(current);

            if (string.Equals(current, root, PathRules.Comparison))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, PathRules.Comparison))
            {
                break;
            }

            current = parent;
        }

        created.Reverse();

        foreach (var path in created)
        {
            _disk.CreateDirectory(path);
        }

        return created;
    }

    /// <summary>
    /// Moves, copies or links the file, and reports which of the three happened.
    /// <paramref name="hardLinked"/> is true only when the library holds the download's own inode.
    /// </summary>
    private TransferMode Transfer(string source, string target, TransferMode mode, out bool hardLinked)
    {
        hardLinked = false;

        switch (mode)
        {
            case TransferMode.Move:
                try
                {
                    _disk.MoveFile(source, target);
                }
                catch (IOException) when (!_disk.FileExists(target) && !_disk.AreSameFile(source, target))
                {
                    // Another device under the library: .NET normally handles EXDEV itself, and the
                    // filter above keeps this fallback for the cases it does not. The filter is also
                    // what keeps a plain "target is taken" failure from quietly copying.
                    _disk.CopyFile(source, target);

                    if (_disk.GetFileSize(target) != _disk.GetFileSize(source))
                    {
                        // What landed is not the download. Drop it and keep the download.
                        _disk.DeleteFile(target);

                        throw new IOException(
                            $"Copied '{source}' to '{target}' with the wrong size; the download was kept.");
                    }

                    try
                    {
                        _disk.DeleteFile(source);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new PlacementTransferException(
                            $"Placed '{target}', but could not remove the download '{source}': {exception.Message}",
                            target);
                    }
                }

                return TransferMode.Move;

            case TransferMode.Copy:
                _disk.CopyFile(source, target);
                return TransferMode.Copy;

            case TransferMode.HardLinkOrCopy:
                hardLinked = _disk.TryCreateHardLink(source, target);

                if (!hardLinked)
                {
                    // Another device, a file system without hard links, or Windows refusing: a copy
                    // is the answer, and the source keeps its own blocks.
                    _disk.CopyFile(source, target);
                }

                return TransferMode.HardLinkOrCopy;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transfer mode.");
        }
    }

    /// <summary>
    /// Puts a recycled file back where it came from, so a failed placement leaves the library as it
    /// found it. Returns the bin path when the file could not go back — or null when there is
    /// nothing left to report.
    /// </summary>
    private string? RollBack(string? recycled, string? replaces)
    {
        if (string.IsNullOrEmpty(recycled) || string.IsNullOrEmpty(replaces))
        {
            return recycled;
        }

        try
        {
            _disk.MoveFile(recycled, replaces);
            return null;
        }
        catch (IOException exception)
        {
            return RollBackFailed(recycled, replaces, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return RollBackFailed(recycled, replaces, exception.Message);
        }
    }

    private string RollBackFailed(string recycled, string replaces, string reason)
    {
        FilePlacerLog.RollBackFailed(_logger, recycled, replaces, reason);

        return recycled;
    }

    /// <summary>Applies the configured modes to the placed file and to the folders it created.</summary>
    private void ApplyPermissions(string target, IReadOnlyList<string> createdDirectories, bool hardLinked)
    {
        var options = _options.CurrentValue;

        if (!options.SetPermissions)
        {
            return;
        }

        // A hard link carries the download's own inode: a mode set here would be set on the download
        // too, so the file keeps whatever the download gave it. The folders are Wondarr's own.
        if (!hardLinked)
        {
            _disk.SetUnixFileMode(target, ImportOptions.ParseMode(options.FileMode));
        }

        var folderMode = ImportOptions.ParseMode(options.FolderMode);

        foreach (var directory in createdDirectories)
        {
            _disk.SetUnixFileMode(directory, folderMode);
        }
    }

    private static PlacementResult Failed(string error, string? recycled = null, string? finalPath = null) =>
        new(false, finalPath, recycled, null, error);

    /// <summary>The file reached the target, and the step after it failed: the target stays.</summary>
    private sealed class PlacementTransferException(string message, string finalPath) : IOException(message)
    {
        /// <summary>Where the file did land.</summary>
        internal string FinalPath { get; } = finalPath;
    }
}

/// <summary>What the placer writes to the log. Source-generated, so a disabled log costs nothing.</summary>
internal static partial class FilePlacerLog
{
    /// <summary>One line per placement: where the file came from, where it went, how.</summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Placed {Source} at {Target} using {Mode}")]
    internal static partial void Placed(ILogger logger, string source, string target, TransferMode mode);

    /// <summary>A recycled file could not be put back after a later step failed.</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not put the recycled file {Recycled} back at {Target}: {Reason}")]
    internal static partial void RollBackFailed(ILogger logger, string recycled, string target, string reason);
}
