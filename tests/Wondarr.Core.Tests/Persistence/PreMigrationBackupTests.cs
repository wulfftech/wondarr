using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core;
using Wondarr.Core.Backup;
using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>
/// The backup <see cref="DatabaseMigrator"/> takes before it migrates an existing database, starting
/// from a real database written by the Phase 0 image (<c>tests/fixtures/migrations</c>).
/// </summary>
public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-tests", "premigration-" + Guid.NewGuid().ToString("N"));
    private readonly WondarrPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero));

    public PreMigrationBackupTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WondarrPaths(_root);
        File.WriteAllText(_paths.ConfigFile, "server:\n  api_key: test\n");
    }

    [Fact]
    public async Task An_existing_database_is_backed_up_before_it_is_migrated_and_the_version_is_recorded()
    {
        UseThePhase0Database();

        await MigrateAsync(RealBackups());

        var backups = Directory.GetFiles(Path.Combine(_paths.BackupsDir, "scheduled"), "*.zip");
        backups.Should().ContainSingle().Which.Should().Contain("wondarr_pre-unknown_", "no version was recorded yet");
        (await ReadUserVersionAsync()).Should().Be(DatabaseMigrator.EncodeVersion(BackupService.CurrentVersion));

        // The next start has nothing to migrate and takes no second backup.
        await MigrateAsync(RealBackups());
        Directory.GetFiles(Path.Combine(_paths.BackupsDir, "scheduled"), "*.zip").Should().HaveCount(1);
    }

    [Fact]
    public async Task The_backup_is_labelled_with_the_version_that_last_ran_the_database()
    {
        UseThePhase0Database();
        Execute($"PRAGMA user_version = {DatabaseMigrator.EncodeVersion("0.0.9")};");

        await MigrateAsync(RealBackups());

        Directory.GetFiles(Path.Combine(_paths.BackupsDir, "scheduled"), "wondarr_pre-0.0.9_*.zip").Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_backup_stops_the_migration()
    {
        UseThePhase0Database();
        var before = await ReadHistoryAsync();
        var backups = Substitute.For<IBackupService>();
        backups.CreatePreMigrationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<BackupItem>(_ => throw new IOException("the disk is full"));

        var act = () => MigrateAsync(backups);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("the disk is full");
        (await ReadHistoryAsync()).Should().Equal(before, "no migration may run without a backup");
    }

    [Fact]
    public async Task Without_a_backup_service_an_existing_database_is_not_migrated()
    {
        UseThePhase0Database();
        var before = await ReadHistoryAsync();

        var act = () => MigrateAsync(backups: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReadHistoryAsync()).Should().Equal(before);
    }

    [Theory]
    [InlineData("0.1.1", 1_001)]
    [InlineData("1.20.300", 1_020_300)]
    [InlineData("0.2.0-rc.1+abc123", 2_000)]
    [InlineData("0.0.0", 0)]
    [InlineData("not a version", 0)]
    public void A_version_survives_the_round_trip_through_user_version(string version, int encoded)
    {
        DatabaseMigrator.EncodeVersion(version).Should().Be(encoded);

        if (encoded > 0)
        {
            DatabaseMigrator.DecodeVersion(encoded).Should().Be(version.Split('-', '+')[0]);
        }
        else
        {
            DatabaseMigrator.DecodeVersion(encoded).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_fresh_database_needs_no_backup()
    {
        var backups = Substitute.For<IBackupService>();

        await MigrateAsync(backups);

        await backups.DidNotReceive().CreatePreMigrationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        File.Exists(_paths.DatabaseFile).Should().BeTrue();
    }

    [Fact]
    public async Task The_health_check_reports_an_error_for_a_database_with_unknown_migrations()
    {
        await MigrateAsync(backups: null);
        Execute("INSERT INTO \"__EFMigrationsHistory\" VALUES ('29990101000000_FromTheFuture', '99.0.0');");

        await using var context = CreateContext();
        var result = await new Wondarr.Core.HealthCheck.DatabaseHealthCheck(context).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(Wondarr.Core.HealthCheck.HealthCheckResult.Error);
        result.Message.Should().Contain("newer Wondarr").And.Contain("29990101000000_FromTheFuture");
    }

    [Fact]
    public async Task The_app_connections_set_a_sixty_second_busy_timeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrPersistence($"Data Source={_paths.DatabaseFile}");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(60_000);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder is cleaned by the operating system anyway.
        }
    }

    private BackupService RealBackups() => new(
        _paths,
        Options.Create(new BackupOptions()),
        _time,
        NullLogger<BackupService>.Instance);

    private WondarrDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<WondarrDbContext>()
                .UseSqlite($"Data Source={_paths.DatabaseFile}")
                .UseSnakeCaseNamingConvention()
                .Options,
            _time);

    private async Task MigrateAsync(IBackupService? backups)
    {
        await using var context = CreateContext();
        await new DatabaseMigrator(context, NullLogger<DatabaseMigrator>.Instance, backups).MigrateAsync(CancellationToken.None);
    }

    private void UseThePhase0Database() =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "migrations", "phase0-compilarr.db"), _paths.DatabaseFile);

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task<List<string>> ReadHistoryAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"__EFMigrationsHistory\" ORDER BY 1;";

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<long> ReadUserVersionAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
