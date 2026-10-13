using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

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

    [Fact]
    public async Task The_next_run_is_the_triggers_next_fire_time_even_after_a_run_now()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        // Quartz keeps firing on its own schedule, whatever time a run ended: a "run now" does not move
        // the fire. (Real clock: the scheduler would move a start in the past to "now".)
        var inFiveMinutes = DateTimeOffset.UtcNow.AddMinutes(5);
        var fire = new DateTimeOffset(inFiveMinutes.Year, inFiveMinutes.Month, inFiveMinutes.Day, inFiveMinutes.Hour, inFiveMinutes.Minute, inFiveMinutes.Second, TimeSpan.Zero);
        await using var quartz = await SchedulerWithTriggerAsync("Heartbeat", fire);
        var updater = new JobTableUpdater(context.DbContext, quartz.GetRequiredService<ISchedulerFactory>());

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

        (await context.ReadJobAsync("Heartbeat"))!.NextRunAt.Should().Be(fire.UtcDateTime);
    }

    [Fact]
    public async Task A_task_the_scheduler_has_no_trigger_for_falls_back_to_the_end_plus_the_interval()
    {
        await using var context = await TestContext.CreateAsync();
        await context.InsertJobAsync("Heartbeat", TimeSpan.FromMinutes(1));

        await using var quartz = await SchedulerWithTriggerAsync("CheckHealth", new DateTimeOffset(Ended.AddSeconds(40), TimeSpan.Zero));
        var updater = new JobTableUpdater(context.DbContext, quartz.GetRequiredService<ISchedulerFactory>());

        await updater.HandleAsync(
            new CommandUpdatedEvent(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                EndedAt = Ended,
            }),
            CancellationToken.None);

        (await context.ReadJobAsync("Heartbeat"))!.NextRunAt.Should().Be(Ended + TimeSpan.FromMinutes(1));
    }

    /// <summary>A real, unstarted scheduler holding one trigger, named as the scheduled-task service names it.</summary>
    private static async Task<ServiceProvider> SchedulerWithTriggerAsync(string commandName, DateTimeOffset nextFire)
    {
        var services = new ServiceCollection();
        services.AddQuartz();
        var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();

        var detail = JobBuilder.Create<EnqueueCommandJob>().WithIdentity(commandName).Build();
        var trigger = TriggerBuilder.Create()
            .ForJob(detail)
            .WithIdentity(ScheduledTaskService.TriggerKeyFor(commandName))
            .StartAt(nextFire)
            .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
            .Build();

        await scheduler.ScheduleJob(detail, trigger);

        return provider;
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
            DbContext = provider.GetRequiredService<WondarrDbContext>();
        }

        public WondarrDbContext DbContext { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWondarrPersistence($"Data Source={Path.Combine(directory, "wondarr.db")}");

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

            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(_directory, "wondarr.db")}"));

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
