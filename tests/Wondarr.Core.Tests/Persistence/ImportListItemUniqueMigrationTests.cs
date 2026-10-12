using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>
/// R-020: the migration that adds the unique <c>(import_list_id, external_id)</c> index first removes the
/// duplicate rows overlapping syncs left behind, keeping the oldest row of each pair and handing it
/// the song and state only a duplicate held.
/// </summary>
public sealed class ImportListItemUniqueMigrationTests
{
    private const string PreviousMigration = "20261011070643_AddCompactMoveAttempts";

    [Fact]
    public async Task Duplicates_are_removed_losslessly_before_the_unique_index_is_created()
    {
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        await ExecuteAsync(
            database,
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO import_list (id, type, name, settings, policy, quality_profile_id, library_id, enabled,
                                     sync_interval_hours, plex_playlist, m3u_export, created_at, updated_at)
            VALUES (1, 'fake', 'One', '{}', 'Mirror', 1, 1, 1, 24, 0, 0, '2026-10-01', '2026-10-01'),
                   (2, 'fake', 'Two', '{}', 'Mirror', 1, 1, 1, 24, 0, 0, '2026-10-01', '2026-10-01');
            INSERT INTO import_list_item (id, import_list_id, external_id, position, raw, state, candidates, song_id, reason, created_at, updated_at)
            VALUES (1, 1, 'a', 0, '{}', 'Pending', '[]', NULL, NULL, '2026-10-01', '2026-10-01'),
                   (2, 1, 'a', 0, '{}', 'Added', '[]', 7, 'Already in the library', '2026-10-02', '2026-10-02'),
                   (3, 1, 'a', 0, '{}', 'Added', '[]', 8, NULL, '2026-10-03', '2026-10-03'),
                   (4, 1, 'b', 1, '{}', 'Added', '[]', 9, NULL, '2026-10-01', '2026-10-01'),
                   (5, 1, 'b', 1, '{}', 'Pending', '[]', NULL, NULL, '2026-10-02', '2026-10-02'),
                   (6, 1, 'c', 2, '{}', 'Unresolved', '[]', NULL, 'No match', '2026-10-01', '2026-10-01'),
                   (7, 2, 'a', 0, '{}', 'Pending', '[]', NULL, NULL, '2026-10-01', '2026-10-01');
            """);

        // The pre-migration backup is the migrator's business (and needs a backup service); the
        // migration itself is what is under test.
        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        var rows = await database.ReadStringsAsync(
            "SELECT id || '|' || import_list_id || '|' || external_id || '|' || state || '|' || IFNULL(song_id, '-') || '|' || IFNULL(reason, '-') "
            + "FROM import_list_item ORDER BY id;");
        rows.Should().Equal(
            "1|1|a|Added|7|Already in the library",
            "4|1|b|Added|9|-",
            "6|1|c|Unresolved|-|No match",
            "7|2|a|Pending|-|-");

        // The unique index is there.
        (await database.ReadStringsAsync(
            "SELECT name FROM pragma_index_list('import_list_item') WHERE \"unique\" = 1;"))
            .Should().Contain("ix_import_list_item_import_list_id_external_id");

        // The new columns start at their defaults.
        (await database.ReadStringsAsync("SELECT policy_unmonitored FROM import_list_item WHERE id = 1;"))
            .Should().Equal("0");
        (await database.ReadStringsAsync("SELECT last_complete_read_count || '|' || IFNULL(suspect_read_message, '-') FROM import_list WHERE id = 1;"))
            .Should().Equal("0|-");
    }

    private static async Task ExecuteAsync(SqliteTestDatabase database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database.FilePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
