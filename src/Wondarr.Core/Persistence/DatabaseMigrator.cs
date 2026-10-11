using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Backup;

namespace Wondarr.Core.Persistence;

/// <summary>
/// Brings the SQLite database up to date and switches it to write-ahead logging. Before it
/// migrates an existing database it writes a backup, and it will not migrate without one.
/// </summary>
/// <remarks>
/// The version that last ran the database is kept in <c>PRAGMA user_version</c> (as
/// <c>major * 1 000 000 + minor * 1 000 + patch</c>; a prerelease suffix is dropped), not in a
/// settings row: it needs no schema, it cannot collide with a migration, and it travels inside the
/// database file through a backup and a restore.
/// </remarks>
public sealed partial class DatabaseMigrator
{
    private readonly WondarrDbContext _context;
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly Func<IBackupService?> _backups;

    /// <summary>Initialises a new instance of the <see cref="DatabaseMigrator"/> class.</summary>
    /// <param name="context">The database to migrate.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="backups">
    /// Writes the pre-migration backup. Without it an existing database with pending migrations is
    /// not migrated at all; a fresh database does not need one.
    /// </param>
    public DatabaseMigrator(WondarrDbContext context, ILogger<DatabaseMigrator> logger, IBackupService? backups = null)
        : this(context, logger, () => backups)
    {
    }

    /// <summary>
    /// Initialises a new instance whose backup service is only looked up when a backup is needed,
    /// so a host that never migrates an existing database never has to construct one.
    /// </summary>
    /// <param name="context">The database to migrate.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="backups">Finds the backup service, or returns <see langword="null"/> when there is none.</param>
    public DatabaseMigrator(WondarrDbContext context, ILogger<DatabaseMigrator> logger, Func<IBackupService?> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);

        _context = context;
        _logger = logger;
        _backups = backups;
    }

    /// <summary>The <c>user_version</c> that encodes <paramref name="version"/>, or 0 when it has no usable numeric part.</summary>
    /// <param name="version">A version such as <c>0.1.1</c> or <c>0.2.0-rc.1+abc</c>.</param>
    public static int EncodeVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var match = NumericVersion().Match(version);

        if (!match.Success)
        {
            return 0;
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        var patch = int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture);

        return major is > 2000 || minor > 999 || patch > 999
            ? 0
            : (major * 1_000_000) + (minor * 1_000) + patch;
    }

    /// <summary>The version string a <c>user_version</c> stands for, or <see langword="null"/> when it is 0.</summary>
    /// <param name="encoded">The value of <c>PRAGMA user_version</c>.</param>
    public static string? DecodeVersion(long encoded) => encoded <= 0
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"{encoded / 1_000_000}.{encoded / 1_000 % 1_000}.{encoded % 1_000}");

    /// <summary>
    /// Backs up an existing database that has pending migrations, applies them, then enables WAL
    /// journalling. Running it twice on the same database is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <exception cref="InvalidOperationException">The pre-migration backup could not be written, so nothing was migrated.</exception>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var applied = (await _context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var pending = (await _context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        var newest = KnownMigrations.NewestUnknown(applied);
        if (newest is not null)
        {
            LogNewerDatabase(newest);
        }

        // A database with no history table is new: there is nothing to lose.
        if (applied.Count > 0 && pending.Count > 0)
        {
            await BackUpBeforeMigratingAsync(pending.Count, cancellationToken).ConfigureAwait(false);
        }

        await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            LogJournalMode(mode ?? "unknown");

            await RecordVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task BackUpBeforeMigratingAsync(int pendingCount, CancellationToken cancellationToken)
    {
        var backups = _backups() ?? throw new InvalidOperationException(
            "The database has pending migrations but no backup service is available, so it was not migrated.");

        var previous = await ReadLastRunVersionAsync(cancellationToken).ConfigureAwait(false) ?? "unknown";

        try
        {
            var backup = await backups.CreatePreMigrationAsync(previous, cancellationToken).ConfigureAwait(false);
            LogBackedUp(pendingCount, backup.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogBackupFailed(exception, pendingCount);

            throw new InvalidOperationException(
                $"The database was not migrated because its pre-migration backup failed: {exception.Message}",
                exception);
        }
    }

    /// <summary>Reads the version that last ran the database, or <see langword="null"/> when none was recorded.</summary>
    private async Task<string?> ReadLastRunVersionAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return DecodeVersion(await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false));
        }
        catch (DbException)
        {
            // Unreadable: the backup's label says unknown, and the backup is still taken.
            return null;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Records the running version, so the next upgrade can label its backup with it.</summary>
    private async Task RecordVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var encoded = EncodeVersion(BackupService.CurrentVersion);

        try
        {
            if (encoded > 0 && await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false) != encoded)
            {
                await using var command = connection.CreateCommand();

                // PRAGMA takes no parameters; the value is an integer this method computed.
                command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {encoded};");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException exception)
        {
            LogVersionNotRecorded(exception);
        }
    }

    private static async Task<long> ReadUserVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)")]
    private static partial Regex NumericVersion();

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the database before applying {Count} pending migration(s): {Backup}")]
    private partial void LogBackedUp(int count, string backup);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The pre-migration backup failed, so the {Count} pending migration(s) were NOT applied and Wondarr will not start")]
    private partial void LogBackupFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "This database was written by a newer Wondarr: it has the migration {Migration}, which this version does not know")]
    private partial void LogNewerDatabase(string migration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record which Wondarr version ran the database")]
    private partial void LogVersionNotRecorded(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "SQLite journal mode is {JournalMode}")]
    private partial void LogJournalMode(string journalMode);
}
