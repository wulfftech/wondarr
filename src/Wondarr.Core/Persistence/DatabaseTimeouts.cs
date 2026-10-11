using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Wondarr.Core.Persistence;

/// <summary>
/// How long the app's SQLite connections wait for a lock.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite leaves SQLite's own <c>busy_timeout</c> at 0 and retries a busy statement
/// itself for as long as the connection string's <c>Default Timeout</c> allows (30 seconds unless
/// set). The context's connection string is left as it is (a different string is a different
/// connection pool, and the tests clear pools by the plain string); instead
/// <see cref="BusyTimeoutInterceptor"/> sets <c>PRAGMA busy_timeout</c> to <see cref="BusySeconds"/>
/// on every connection the context opens, so SQLite's own waits (a checkpoint, a <c>VACUUM</c>
/// against a reader) get that budget. The backup service's private connection sets
/// <c>Default Timeout</c> itself.
/// </remarks>
public static class DatabaseTimeouts
{
    /// <summary>The busy timeout, in seconds, the app's connections use.</summary>
    public const int BusySeconds = 60;
}

/// <summary>Sets <c>PRAGMA busy_timeout</c> on every connection the context opens.</summary>
public sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
{
    private const string Sql = "PRAGMA busy_timeout = 60000;";

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
