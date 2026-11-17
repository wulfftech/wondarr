using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>The disk did not answer within <see cref="LibraryRoots.CheckTimeout"/>, so its share may be hung.</summary>
public sealed class MusicVideoTimeoutException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoTimeoutException"/> class.</summary>
    public MusicVideoTimeoutException()
        : base("The music video folder did not respond")
    {
    }
}

/// <summary>Runs disk calls that could hang on the thread pool, bounded like the library root check.</summary>
internal static class MusicVideoDisk
{
    /// <summary>Runs <paramref name="work"/> and waits at most <see cref="LibraryRoots.CheckTimeout"/> for it.</summary>
    /// <exception cref="MusicVideoTimeoutException">The call did not return in time.</exception>
    public static async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(work, CancellationToken.None)
                .WaitAsync(LibraryRoots.CheckTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new MusicVideoTimeoutException();
        }
    }
}

/// <summary>The rules every writer of the music video root shares.</summary>
public static class MusicVideoRootRules
{
    /// <summary>Whether two folders are the same, or one holds the other. A path that is not usable overlaps nothing.</summary>
    /// <param name="first">One folder.</param>
    /// <param name="second">The other folder.</param>
    public static bool Overlaps(string first, string second)
    {
        try
        {
            return PathRules.IsInside(second, first) || PathRules.IsInside(first, second);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why <paramref name="root"/> cannot be the video root: not fully qualified for this OS, Wondarr's own data
    /// directory, or overlapping a library, a reference library or the recycle bin. Empty when it is fine.
    /// </summary>
    /// <param name="root">The folder.</param>
    /// <param name="database">Where the library and reference-library roots are.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    /// <param name="importOptions">Where the recycle bin is.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    public static async Task<List<string>> ProblemsAsync(
        string root,
        WondarrDbContext database,
        WondarrPaths paths,
        IOptionsMonitor<ImportOptions> importOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(importOptions);

        if (!Path.IsPathFullyQualified(root))
        {
            return [@"rootPath must be an absolute path, for example /data/videos or D:\Videos."];
        }

        var problems = new List<string>();

        try
        {
            if (ProtectedPaths.Reason(root, paths) is { } reason)
            {
                problems.Add($"rootPath {reason}, where Wondarr keeps its API key, passwords and database.");
            }

            var libraries = await database.Libraries
                .AsNoTracking()
                .Select(library => new { library.Name, library.RootPath })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var library in libraries.Where(library => Overlaps(root, library.RootPath)))
            {
                problems.Add($"rootPath overlaps the library '{library.Name}'; videos need a folder of their own.");
            }

            var references = await database.ReferenceLibraries
                .AsNoTracking()
                .Select(reference => new { reference.Name, reference.RootPath })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var reference in references.Where(reference => Overlaps(root, reference.RootPath)))
            {
                problems.Add($"rootPath overlaps the reference library '{reference.Name}'; Wondarr never writes into one.");
            }

            var bin = importOptions.CurrentValue.ResolveRecycleBinPath(paths);

            if (Overlaps(root, bin))
            {
                problems.Add($"rootPath overlaps the recycle bin '{bin}'; the bin's cleanup would delete the videos.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add("rootPath is not a usable path.");
        }

        return problems;
    }
}
