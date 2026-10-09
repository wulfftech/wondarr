using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>
/// Starts from a real database written by the Phase 0 image (<c>tests/fixtures/migrations</c>) and
/// proves the upgrade a real user takes: adopt the pre-rename file, apply every later migration,
/// keep Phase 0's rows and end up with a database the current model can use.
/// </summary>
public sealed class Phase0UpgradeTests
{
    [Fact]
    public async Task The_phase_0_database_is_adopted_and_upgraded_to_every_migration()
    {
        using var upgrade = await Phase0Database.CreateAsync();

        await using var context = upgrade.CreateContext();
        var all = context.Database.GetMigrations().ToList();
        all.Should().HaveCountGreaterThan(2);

        var applied = await upgrade.ReadHistoryAsync();
        applied.Should().Equal(all, "every migration in the assembly is recorded, in order");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Adopting_the_legacy_file_renames_it()
    {
        using var upgrade = await Phase0Database.CreateAsync(migrate: false);

        File.Exists(upgrade.Paths.DatabaseFile).Should().BeTrue();
        File.Exists(upgrade.Paths.LegacyDatabaseFile).Should().BeFalse();
    }

    [Fact]
    public async Task Phase_0_rows_survive_the_upgrade()
    {
        using var upgrade = await Phase0Database.CreateAsync();
        await using var context = upgrade.CreateContext();

        var settings = await context.Settings.ToListAsync();
        var setting = settings.Should().ContainSingle().Subject;
        setting.Key.Should().Be("slskd.runtime");
        setting.Value.Should().Contain("phase0-fixture-not-a-secret").And.Contain("\"webUsername\":\"compilarr\"");

        var jobs = await context.Jobs.OrderBy(job => job.Id).ToListAsync();
        jobs.Select(job => (job.Name, job.Interval)).Should().Equal(
            ("Heartbeat", (TimeSpan?)TimeSpan.FromMinutes(1)),
            ("CheckHealth", (TimeSpan?)TimeSpan.FromMinutes(15)));

        var commands = await context.Commands.OrderBy(command => command.Id).ToListAsync();
        commands.Select(command => (command.Name, command.Status, command.Trigger)).Should().Equal(
            ("CheckHealth", CommandStatus.Completed, CommandTrigger.Scheduled),
            ("Heartbeat", CommandStatus.Completed, CommandTrigger.Scheduled),
            ("Heartbeat", CommandStatus.Completed, CommandTrigger.Scheduled),
            ("CheckHealth", CommandStatus.Completed, CommandTrigger.Manual));
    }

    [Fact]
    public async Task The_seed_data_from_later_migrations_exists()
    {
        using var upgrade = await Phase0Database.CreateAsync();
        await using var context = upgrade.CreateContext();

        (await context.Qualities.CountAsync()).Should().Be(SeedData.Qualities.Length);
        var flac = await context.Qualities.SingleAsync(quality => quality.Name == "FLAC");
        flac.Lossless.Should().BeTrue();
        var opus = await context.Qualities.SingleAsync(quality => quality.Id == SeedData.Opus160QualityId);
        opus.Name.Should().Be("OPUS-160");

        var profiles = await context.QualityProfiles.OrderBy(profile => profile.Id).ToListAsync();
        profiles.Select(profile => profile.Name).Should().Equal("Standard 320", "Lossless");
        profiles[0].Id.Should().Be(SeedData.StandardProfileId);
        profiles[1].Id.Should().Be(SeedData.LosslessProfileId);

        var library = await context.Libraries.SingleAsync();
        library.Name.Should().Be("Music");
        library.IsDefault.Should().BeTrue();
        library.Id.Should().Be(SeedData.DefaultLibraryId);
    }

    [Fact]
    public async Task A_song_can_be_added_and_read_back_on_the_upgraded_file()
    {
        using var upgrade = await Phase0Database.CreateAsync();

        long songId;
        await using (var context = upgrade.CreateContext())
        {
            var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
            var song = new Song
            {
                Title = "One More Time",
                ArtistCredit = "Daft Punk",
                PrimaryArtist = artist,
                QualityProfileId = SeedData.StandardProfileId,
                LibraryId = SeedData.DefaultLibraryId,
                AddedBy = "api",
            };
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
        }

        await using var readContext = upgrade.CreateContext();
        var stored = await readContext.Songs.Include(song => song.PrimaryArtist).SingleAsync(song => song.Id == songId);
        stored.Title.Should().Be("One More Time");
        stored.PrimaryArtist.Name.Should().Be("Daft Punk");
        stored.QualityProfileId.Should().Be(SeedData.StandardProfileId);
        stored.LibraryId.Should().Be(SeedData.DefaultLibraryId);
    }

    [Fact]
    public async Task A_second_run_of_the_migrator_changes_nothing()
    {
        using var upgrade = await Phase0Database.CreateAsync();
        var before = await upgrade.ReadHistoryAsync();

        var act = async () => await upgrade.MigrateAsync();
        await act.Should().NotThrowAsync();

        (await upgrade.ReadHistoryAsync()).Should().Equal(before);
        await using var context = upgrade.CreateContext();
        (await context.Jobs.CountAsync()).Should().Be(2);
        (await context.Commands.CountAsync()).Should().Be(4);
    }

    /// <summary>A temp config directory holding the fixture as <c>compilarr.db</c>, adopted and optionally migrated.</summary>
    private sealed class Phase0Database : IDisposable
    {
        private readonly string _directory;

        private Phase0Database(string directory)
        {
            _directory = directory;
            Paths = new WondarrPaths(directory);
        }

        public WondarrPaths Paths { get; }

        public static async Task<Phase0Database> CreateAsync(bool migrate = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", "phase0-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new Phase0Database(directory);

            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "migrations", "phase0-compilarr.db");
            File.Copy(fixture, database.Paths.LegacyDatabaseFile);

            database.Paths.AdoptLegacyDatabase().Should().BeTrue();

            if (migrate)
            {
                await database.MigrateAsync();
            }

            return database;
        }

        public WondarrDbContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<WondarrDbContext>()
                    .UseSqlite($"Data Source={Paths.DatabaseFile}")
                    .UseSnakeCaseNamingConvention()
                    .Options,
                new FakeTimeProvider());

        public async Task MigrateAsync()
        {
            await using var context = CreateContext();
            await new DatabaseMigrator(context, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        }

        /// <summary>The migration ids recorded in <c>__EFMigrationsHistory</c>, oldest first.</summary>
        public async Task<List<string>> ReadHistoryAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.DatabaseFile}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();

            // The snake_case convention renames the history columns too, so read positionally.
            command.CommandText = "SELECT * FROM \"__EFMigrationsHistory\" ORDER BY 1;";

            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
