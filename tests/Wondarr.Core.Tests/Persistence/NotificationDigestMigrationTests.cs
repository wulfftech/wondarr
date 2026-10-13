using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>
/// M-28: the migration that adds the email digest table is additive. A database seeded by the previous
/// version keeps its notifications, gains an empty table, and the table follows its notification's delete.
/// </summary>
public sealed class NotificationDigestMigrationTests
{
    private const string PreviousMigration = "20261012035333_HardenImportListSync";

    [Fact]
    public async Task A_database_from_the_previous_version_keeps_its_notifications_and_gains_the_digest_table()
    {
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await database.ReadStringsAsync("SELECT name FROM sqlite_master WHERE name = 'notification_digest_entry';"))
            .Should().BeEmpty("the previous version has no such table");

        await ExecuteAsync(
            database,
            """
            INSERT INTO notification (id, name, type, settings, events, enabled, created_at, updated_at)
            VALUES (1, 'Webhook', 'Webhook', '{"url":"http://hooks.local"}', '["import"]', 1, '2026-10-01', '2026-10-01');
            """);

        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        (await database.ReadStringsAsync("SELECT name FROM notification;")).Should().Equal("Webhook");
        (await database.ReadStringsAsync("SELECT name FROM sqlite_master WHERE name = 'notification_digest_entry';"))
            .Should().Equal("notification_digest_entry");

        await ExecuteAsync(
            database,
            """
            INSERT INTO notification_digest_entry (notification_id, event, song_id, snapshot, created_at, updated_at)
            VALUES (1, 'import', 7, '{}', '2026-10-13', '2026-10-13');
            DELETE FROM notification WHERE id = 1;
            """,
            foreignKeys: true);

        (await database.ReadStringsAsync("SELECT COUNT(*) FROM notification_digest_entry;")).Should().Equal("0");
    }

    private static async Task ExecuteAsync(SqliteTestDatabase database, string sql, bool foreignKeys = false)
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={database.FilePath}"));

        await using var connection = new SqliteConnection(
            $"Data Source={database.FilePath};Pooling=False;Foreign Keys={(foreignKeys ? "True" : "False")}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
