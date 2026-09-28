using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

public class DatabaseMigratorTests
{
    [Fact]
    public async Task MigrateAsync_creates_a_wal_database_with_the_phase_0_tables()
    {
        using var database = new SqliteTestDatabase();

        await database.MigrateAsync(new FakeTimeProvider());

        File.Exists(database.FilePath).Should().BeTrue();

        var journalMode = await database.ReadStringsAsync("PRAGMA journal_mode;");
        journalMode.Should().ContainSingle().Which.Should().Be("wal");

        var tables = await database.ReadStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table';");
        tables.Should().Contain("setting").And.Contain("job").And.Contain("__EFMigrationsHistory");

        var settingColumns = await database.ReadStringsAsync("SELECT name FROM pragma_table_info('setting');");
        settingColumns.Should().Contain("created_at").And.Contain("updated_at");

        var jobColumns = await database.ReadStringsAsync("SELECT name FROM pragma_table_info('job');");
        jobColumns.Should().Contain("last_run_at").And.Contain("next_run_at").And.Contain("created_at");

        // The snake_case convention renames the history columns too, so read positionally.
        var migrations = await database.ReadStringsAsync("SELECT * FROM \"__EFMigrationsHistory\";");
        migrations.Should().Contain(migration => migration.EndsWith("InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_the_migrator_twice_is_a_no_op()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();

        await database.MigrateAsync(timeProvider);

        var act = async () => await database.MigrateAsync(timeProvider);

        await act.Should().NotThrowAsync();

        var migrations = await database.ReadStringsAsync("SELECT * FROM \"__EFMigrationsHistory\";");
        // one history row per migration, however many later tasks add
        migrations.Should().OnlyHaveUniqueItems().And.Contain(migration => migration.EndsWith("InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_records_progress_in_the_journal()
    {
        using var database = new SqliteTestDatabase();

        await using var context = database.CreateContext(new FakeTimeProvider());
        var migrator = new DatabaseMigrator(context, NullLogger<DatabaseMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        var applied = context.Database.GetAppliedMigrations();
        applied.Should().Contain(migration => migration.EndsWith("InitialCreate", StringComparison.Ordinal));
    }
}
