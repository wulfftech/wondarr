using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Organizer;

/// <summary>
/// Puts the marker into the root of a library whose folder exists and looks like the real thing: one
/// whose recorded files are still on the disk, or, with no files on record, one whose folder holds
/// something (an unmounted mount point is empty). It never creates a root and never marks a missing,
/// hung or empty-and-suspicious one; those stay unmarked for the health check and the user to settle.
/// </summary>
public interface ILibraryRootAdopter
{
    /// <summary>Marks every library root that qualifies and is not ready yet.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many roots were marked.</returns>
    Task<int> AdoptAsync(CancellationToken cancellationToken);
}

/// <summary>The one implementation of <see cref="ILibraryRootAdopter"/>.</summary>
public sealed partial class LibraryRootAdopter : ILibraryRootAdopter
{
    /// <summary>How many of a library's latest imports are looked for on the disk.</summary>
    internal const int SampleSize = 20;

    private readonly WondarrDbContext _database;
    private readonly ILibraryRoots _roots;
    private readonly IDiskOperations _disk;
    private readonly ILogger<LibraryRootAdopter> _logger;

    /// <summary>Initialises a new instance of the <see cref="LibraryRootAdopter"/> class.</summary>
    /// <param name="database">Where the libraries and their files are recorded.</param>
    /// <param name="roots">Checks and marks the roots.</param>
    /// <param name="disk">Looks for the recorded files.</param>
    /// <param name="logger">The log sink.</param>
    public LibraryRootAdopter(
        WondarrDbContext database,
        ILibraryRoots roots,
        IDiskOperations disk,
        ILogger<LibraryRootAdopter> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _roots = roots;
        _disk = disk;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> AdoptAsync(CancellationToken cancellationToken)
    {
        var libraries = await _database.Libraries
            .AsNoTracking()
            .OrderBy(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var adopted = 0;

        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await AdoptOneAsync(library, cancellationToken).ConfigureAwait(false))
                {
                    adopted++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad root never aborts the pass (or the health check that runs it).
                LogMarkFailed(_logger, library.Name, library.RootPath, exception);
            }
        }

        return adopted;
    }

    /// <summary>Marks one library's root when the rules allow it; never creates a root.</summary>
    private async Task<bool> AdoptOneAsync(Library library, CancellationToken cancellationToken)
    {
        // Only an existing folder without the marker is ever adopted. A missing root is never marked
        // (that would create it on the host's own disk), and a hung one is left for a later pass.
        if (await _roots.CheckAsync(library.RootPath, cancellationToken).ConfigureAwait(false) != LibraryRootStatus.Unmarked)
        {
            return false;
        }

        var paths = await _database.Songs
            .AsNoTracking()
            .Where(song => song.LibraryId == library.Id
                && song.File != null
                && song.File.SourceType != SourceTypes.Reference)
            .OrderByDescending(song => song.File!.ImportedAt)
            .ThenByDescending(song => song.File!.Id)
            .Select(song => song.File!.Path)
            .Take(SampleSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Files on record: one of the latest must be on the disk. None on record: an unmounted mount
        // point is empty, so the folder must hold something. Otherwise it stays unmarked and the
        // health check reports it; the user can confirm it.
        var plausible = paths.Count > 0
            ? paths.Any(_disk.FileExists)
            : _disk.HasAnyEntry(library.RootPath);

        if (!plausible)
        {
            LogLeftUnmarked(_logger, library.Name, library.RootPath);

            return false;
        }

        if (!_roots.MarkExisting(library.RootPath))
        {
            return false;
        }

        LogAdopted(_logger, library.Name, library.RootPath);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked the root of library {Library} ({Root}) with the Wondarr marker file")]
    private static partial void LogAdopted(ILogger logger, string library, string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library {Library} ({Root}) is an empty folder or holds no recorded file, so its root was not marked; it may be an unmounted share")]
    private static partial void LogLeftUnmarked(ILogger logger, string library, string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mark the root of library {Library} ({Root})")]
    private static partial void LogMarkFailed(ILogger logger, string library, string root, Exception exception);
}
