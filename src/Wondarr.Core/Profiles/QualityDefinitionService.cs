using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Profiles;

/// <summary>Reads the seeded quality ladder.</summary>
public interface IQualityDefinitionService
{
    /// <summary>Lists every quality, ordered by id (which is also ascending rank).</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<Quality>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The quality ladder as the API and the profile editor see it. The rows are seed data with ids that
/// are a public contract, so there is nothing to add, edit or delete here — the endpoint only lists.
/// </summary>
public sealed class QualityDefinitionService : IQualityDefinitionService
{
    private readonly WondarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="QualityDefinitionService"/> class.</summary>
    /// <param name="context">The database context.</param>
    public QualityDefinitionService(WondarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Quality>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Qualities
            .AsNoTracking()
            .OrderBy(quality => quality.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
