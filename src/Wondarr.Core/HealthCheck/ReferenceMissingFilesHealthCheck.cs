using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Warns about a reference library with so many owned files missing at once that its share looks
/// unmounted (<see cref="ReferenceMissingRules"/>). The scan marks those rows missing but releases no
/// song while this holds. The check recounts from the rows, so the warning clears on its own once a
/// later scan finds the files again or the count drops below the threshold.
/// </summary>
public sealed class ReferenceMissingFilesHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="ReferenceMissingFilesHealthCheck"/> class.</summary>
    /// <param name="database">Where the reference files are.</param>
    public ReferenceMissingFilesHealthCheck(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public string Name => nameof(ReferenceMissingFilesHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var counts = await _database.ReferenceFiles
            .AsNoTracking()
            .Where(file => file.SongId != null
                && (file.State == ReferenceFileState.Identified || file.State == ReferenceFileState.Missing))
            .GroupBy(file => file.ReferenceLibraryId)
            .Select(group => new
            {
                LibraryId = group.Key,
                Owned = group.Count(),
                Missing = group.Count(file => file.State == ReferenceFileState.Missing),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var flagged = counts.Where(count => ReferenceMissingRules.IsMountProblem(count.Owned, count.Missing)).ToList();

        if (flagged.Count == 0)
        {
            return new HealthCheck(Name, HealthCheckResult.Ok, "No reference library is missing a suspicious number of files", null);
        }

        var ids = flagged.Select(count => count.LibraryId).ToList();
        var names = await _database.ReferenceLibraries
            .AsNoTracking()
            .Where(library => ids.Contains(library.Id))
            .ToDictionaryAsync(library => library.Id, library => library.Name, cancellationToken)
            .ConfigureAwait(false);

        var parts = flagged.Select(count => string.Concat(
            "'",
            names.GetValueOrDefault(count.LibraryId, "?"),
            "' (",
            count.Missing.ToString(CultureInfo.InvariantCulture),
            " of ",
            count.Owned.ToString(CultureInfo.InvariantCulture),
            " owned files)"));

        return new HealthCheck(
            Name,
            HealthCheckResult.Warning,
            "Many files are missing from the reference library " + string.Join(", ", parts)
            + ". The share may not be mounted, so no song is released to be searched for again until they are back or the count drops.",
            null);
    }
}
