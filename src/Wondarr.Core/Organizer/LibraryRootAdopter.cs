using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Organizer;

/// <summary>
/// Puts the marker into the root of every library that already is what it claims to be: a library with
/// no files of its own (a new or empty one has nothing to protect), or one whose recorded files are
/// still on the disk. A library whose files are all gone is an unmounted share and stays unmarked.
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
            if (_roots.Check(library.RootPath) == LibraryRootStatus.Ready)
            {
                continue;
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

            if (paths.Count > 0
                && !(_disk.DirectoryExists(library.RootPath) && paths.Any(_disk.FileExists)))
            {
                // Files are recorded and none is there: the share is not mounted, so the root is not
                // marked. The health check says so until it is back or the user confirms the folder.
                LogLeftUnmarked(_logger, library.Name, library.RootPath);
                continue;
            }

            try
            {
                _roots.Mark(library.RootPath);
                adopted++;
                LogAdopted(_logger, library.Name, library.RootPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogMarkFailed(_logger, library.Name, library.RootPath, exception);
            }
        }

        return adopted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked the root of library {Library} ({Root}) with the Wondarr marker file")]
    private static partial void LogAdopted(ILogger logger, string library, string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library {Library} ({Root}) holds recorded files but none was found, so its root was not marked; it may be an unmounted share")]
    private static partial void LogLeftUnmarked(ILogger logger, string library, string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mark the root of library {Library} ({Root})")]
    private static partial void LogMarkFailed(ILogger logger, string library, string root, Exception exception);
}
