using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Reports every library whose root folder is missing or carries no marker file: Wondarr writes
/// nothing into it, imports wait and automatic searches skip its songs. It first runs the adoption
/// pass, so a library whose share was down at the first start of this version is marked when it
/// returns. The message holds no counts or times, so the health notification does not repeat itself.
/// </summary>
public sealed class LibraryRootHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;
    private readonly ILibraryRoots _roots;
    private readonly ILibraryRootAdopter _adopter;

    /// <summary>Initialises a new instance of the <see cref="LibraryRootHealthCheck"/> class.</summary>
    /// <param name="database">Where the libraries are.</param>
    /// <param name="roots">Checks the roots on the disk.</param>
    /// <param name="adopter">Marks the roots that qualify before they are checked.</param>
    public LibraryRootHealthCheck(WondarrDbContext database, ILibraryRoots roots, ILibraryRootAdopter adopter)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(adopter);

        _database = database;
        _roots = roots;
        _adopter = adopter;
    }

    /// <inheritdoc />
    public string Name => nameof(LibraryRootHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        await _adopter.AdoptAsync(cancellationToken).ConfigureAwait(false);

        var libraries = await _database.Libraries
            .AsNoTracking()
            .OrderBy(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var problems = new List<string>();

        foreach (var library in libraries)
        {
            var status = _roots.Check(library.RootPath);

            if (status != LibraryRootStatus.Ready)
            {
                problems.Add(_roots.Describe(library, status));
            }
        }

        return problems.Count == 0
            ? new HealthCheck(Name, HealthCheckResult.Ok, "Every library folder is ready", null)
            : new HealthCheck(Name, HealthCheckResult.Error, string.Join(' ', problems), null);
    }
}
