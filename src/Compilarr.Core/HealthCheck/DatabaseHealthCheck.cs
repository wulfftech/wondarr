using System.Data;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compilarr.Core.HealthCheck;

/// <summary>Checks that the SQLite database opens, answers a query and has no pending migrations.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly CompilarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.</summary>
    public DatabaseHealthCheck(CompilarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public string Name => nameof(DatabaseHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Probe connectivity first: a database that cannot be opened would otherwise
            // surface as the much-less-specific "pending migrations" error.
            var connection = _context.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;

            if (opened)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            string version;
            try
            {
                await using var probe = connection.CreateCommand();
                probe.CommandText = "SELECT 1;";
                await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "SELECT sqlite_version();";
                version = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? "unknown";
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }

            var pending = (await _context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();

            if (pending.Count > 0)
            {
                return new HealthCheck(
                    Name,
                    HealthCheckResult.Error,
                    $"Database has {pending.Count} pending migration(s): {string.Join(", ", pending)}",
                    null);
            }

            return new HealthCheck(Name, HealthCheckResult.Ok, $"Database OK (SQLite {version})", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HealthCheck(Name, HealthCheckResult.Error, $"Database is not available: {exception.Message}", null);
        }
    }
}