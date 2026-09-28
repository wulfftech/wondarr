using Compilarr.Core.Jobs;
using Compilarr.Core.Messaging;
using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Core.Tests.Jobs;

/// <summary>
/// The write-back half of the scheduler: the outcome the executor publishes lands on the job row, so
/// the Tasks list is right whether the run was scheduled or asked for by hand.
/// </summary>
public class JobTableUpdaterTests
{
    private static readonly DateTime Ended = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_completed_command_moves_the_next_run_on_and_records_the_result()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        var updater = new JobTableUpdater(context.DbContext);

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                Message = "Heartbeat OK",
                EndedAt = Ended,
            }),
            CancellationToken.None);

        var job = await context.ReadJobAsync("Heartbeat");

        job!.LastRunAt.Should().Be(Ended);
        job.NextRunAt.Should().Be(Ended + TimeSpan.FromMinutes(1));
        job.LastResult.Should().Be("successful: Heartbeat OK");
    }

    [Fact]
    public async Task A_failed_command_records_the_exception_as_unsuccessful()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("CheckHealth", TimeSpan.FromMinutes(15));

        var updater = new JobTableUpdater(context.DbContext);

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "CheckHealth",
                Status = CommandStatus.Failed,
                Result = CommandResult.Unsuccessful,
                Exception = "the indexer refused the connection",
                EndedAt = Ended,
            }),
            CancellationToken.None);

        var job = await context.ReadJobAsync("CheckHealth");

        job!.LastResult.Should().Be("unsuccessful: the indexer refused the connection");
        job.NextRunAt.Should().Be(Ended + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task A_long_result_is_trimmed_to_what_the_table_keeps()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        var updater = new JobTableUpdater(context.DbContext);

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                Message = new string('x', 900),
                EndedAt = Ended,
            }),
            CancellationToken.None);

        (await context.ReadJobAsync("Heartbeat"))!.LastResult.Should().HaveLength(500);
    }

    [Fact]
    public async Task A_command_with_no_job_row_is_ignored()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        var updater = new JobTableUpdater(context.DbContext);

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "RefreshArtist",
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                Message = "done",
                EndedAt = Ended,
            }),
            CancellationToken.None);

        var job = await context.ReadJobAsync("Heartbeat");

        job!.LastRunAt.Should().BeNull();
        job.LastResult.Should().BeNull("the updater follows a schedule, it does not create one");
    }

    [Fact]
    public async Task A_command_that_has_not_finished_leaves_the_row_alone()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        var updater = new JobTableUpdater(context.DbContext);

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Started,
                Result = CommandResult.Unknown,
                Message = "running",
            }),
            CancellationToken.None);

        var job = await context.ReadJobAsync("Heartbeat");

        job!.LastRunAt.Should().BeNull();
        job.LastResult.Should().BeNull();
    }

    /// <summary>A migrated SQLite container plus the context the updater writes through.</summary>
    private sealed class TestContext : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _directory;

        private TestContext(ServiceProvider provider, string directory)
        {
            _provider = provider;
            _directory = directory;
            DbContext = provider.GetRequiredService<CompilarrDbContext>();
        }

        public CompilarrDbContext DbContext { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "compilarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCompilarrPersistence($"Data Source={Path.Combine(directory, "compilarr.db")}");

            var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
            }

            return new TestContext(provider, directory);
        }

        public async Task InsertJobAsync(string name, TimeSpan interval)
        {
            DbContext.Jobs.Add(new Job { Name = name, Interval = interval });
            await DbContext.SaveChangesAsync(CancellationToken.None);
        }

        public async Task<Job?> ReadJobAsync(string name)
        {
            DbContext.ChangeTracker.Clear();

            return await DbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(job => job.Name == name);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(_directory, "compilarr.db")}"));

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
