using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Persistence;

/// <summary>
/// Brings the SQLite database up to date and switches it to write-ahead logging.
/// </summary>
public sealed partial class DatabaseMigrator
{
    private readonly CompilarrDbContext _context;
    private readonly ILogger<DatabaseMigrator> _logger;

    /// <summary>Initialises a new instance of the <see cref="DatabaseMigrator"/> class.</summary>
    public DatabaseMigrator(CompilarrDbContext context, ILogger<DatabaseMigrator> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Applies every pending migration, then enables WAL journalling.
    /// Running it twice on the same database is a no-op.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            LogJournalMode(mode?.ToString() ?? "unknown");
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SQLite journal mode is {JournalMode}")]
    private partial void LogJournalMode(string journalMode);
}
