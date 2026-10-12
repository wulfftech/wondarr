using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Warns about an import list whose last read was not trusted (R-007): it failed, stopped at a
/// provider's cap, or came back empty or far shorter than the last complete read. That sync added what
/// it found and skipped its removals, so a Mirror or AddAndUnmonitor list is not acting on it. The
/// warning clears by itself on the next complete read.
/// </summary>
public sealed class ImportListReadHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="ImportListReadHealthCheck"/> class.</summary>
    /// <param name="database">Where the lists and their last read are.</param>
    public ImportListReadHealthCheck(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public string Name => nameof(ImportListReadHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var suspect = await _database.ImportLists
            .AsNoTracking()
            .Where(list => list.Enabled && list.SuspectReadMessage != null)
            .OrderBy(list => list.Id)
            .Select(list => new { list.Name, list.SuspectReadMessage })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (suspect.Count == 0)
        {
            return new HealthCheck(Name, HealthCheckResult.Ok, "Every import list was read in full", null);
        }

        return new HealthCheck(
            Name,
            HealthCheckResult.Warning,
            string.Join(" ", suspect.Select(list => $"Import list '{list.Name}': {list.SuspectReadMessage}.")),
            null);
    }
}

/// <summary>
/// Says that songs an import list adds are not searched for yet, because a reference library is still
/// identifying its files (owner question 15). Information only: the search starts by itself when the
/// scan is done.
/// </summary>
public sealed class ImportListScanWaitHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="ImportListScanWaitHealthCheck"/> class.</summary>
    /// <param name="database">Where the lists and the reference libraries are.</param>
    public ImportListScanWaitHealthCheck(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public string Name => nameof(ImportListScanWaitHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var hasList = await _database.ImportLists
            .AsNoTracking()
            .AnyAsync(list => list.Enabled && list.Type != ImportList.PasteType, cancellationToken)
            .ConfigureAwait(false);

        var waiting = hasList
            ? await ReferenceScanWait.PendingLibrariesAsync(_database, cancellationToken).ConfigureAwait(false)
            : [];

        return waiting.Count == 0
            ? new HealthCheck(Name, HealthCheckResult.Ok, "No import list is waiting for a reference scan", null)
            : new HealthCheck(
                Name,
                HealthCheckResult.Notice,
                $"Import list songs wait for the reference scan of {string.Join(", ", waiting)} to finish",
                null);
    }
}
