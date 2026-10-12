using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.ImportLists;

/// <summary>
/// Whether a reference library is still identifying its files (owner question 15). A song an import list
/// adds while that is so is not searched for: the user may already own it as a file the scan has not
/// reached, and a search would download it a second time. Read-only on the reference tables.
/// </summary>
internal static class ReferenceScanWait
{
    /// <summary>
    /// The names of the enabled reference libraries that still hold files the scanner has not identified:
    /// rows in <see cref="ReferenceFileState.Pending"/>, which is also where a file AcoustID could not be
    /// asked about waits for the next scan. Every other state is a finished answer (identified, ambiguous,
    /// unmatched) or a file that is not coming back (missing, unreadable, skipped). A disabled library is
    /// not scanned, so it is not waited for.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The library names in id order; empty when no scan is outstanding.</returns>
    public static async Task<IReadOnlyList<string>> PendingLibrariesAsync(
        WondarrDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await database.ReferenceLibraries
            .AsNoTracking()
            .Where(library => library.Enabled
                && database.ReferenceFiles.Any(file =>
                    file.ReferenceLibraryId == library.Id && file.State == ReferenceFileState.Pending))
            .OrderBy(library => library.Id)
            .Select(library => library.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
