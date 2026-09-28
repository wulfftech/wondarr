using Compilarr.Core.Metadata;
using Compilarr.Core.Jobs;
using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quartz;
using Xunit;

namespace Compilarr.Core.Tests.Jobs;

/// <summary>
/// Covers the start-up half of the scheduler: the rows it writes and the triggers it hands Quartz.
/// The scheduler factory is substituted, so no trigger ever fires and no test waits a real minute.
/// </summary>
public class ScheduledTaskServiceTests
{
    [Fact]
    public async Task A_fresh_database_gets_a_row_and_a_trigger_for_every_task()
    {
        await using var host = await TaskHost.CreateAsync();

        await host.StartAsync();

        var jobs = await host.ReadJobsAsync();

        jobs.Select(job => job.Name).Should().BeEquivalentTo("Heartbeat", "CheckHealth");
        jobs.Should().OnlyContain(job => job.NextRunAt == host.Now + TimeSpan.FromSeconds(10));
        jobs.Single(job => job.Name == "Heartbeat").Interval.Should().Be(TimeSpan.FromMinutes(1));
        jobs.Single(job => job.Name == "CheckHealth").Interval.Should().Be(TimeSpan.FromMinutes(15));
        jobs.Should().OnlyContain(job => job.LastRunAt == null && job.LastResult == null);

        host.Triggers.Should().HaveCount(jobs.Count, "every row gets its own trigger");
        host.Triggers.Select(trigger => trigger.JobKey.Name)
            .Should().BeEquivalentTo("Heartbeat", "CheckHealth");
        host.Triggers.Should().OnlyContain(trigger =>
            trigger.StartTimeUtc == new DateTimeOffset(host.Now + TimeSpan.FromSeconds(10), TimeSpan.Zero));
        host.Triggers.Cast<ISimpleTrigger>()
            .Single(trigger => trigger.JobKey.Name == "Heartbeat")
            .RepeatInterval.Should().Be(TimeSpan.FromMinutes(1));
        host.Triggers.Cast<ISimpleTrigger>()
            .Single(trigger => trigger.JobKey.Name == "CheckHealth")
            .RepeatInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task The_enqueued_job_is_told_which_command_to_queue_and_may_not_run_twice_at_once()
    {
        await using var host = await TaskHost.CreateAsync();

        await host.StartAsync();

        host.Jobs.Single(job => job.Key.Name == "Heartbeat")
            .JobDataMap.GetString(EnqueueCommandJob.CommandNameKey).Should().Be("Heartbeat");
        typeof(EnqueueCommandJob).Should().BeDecoratedWith<DisallowConcurrentExecutionAttribute>();
    }

    [Fact]
    public async Task A_run_that_is_not_due_yet_resumes_from_last_run_plus_the_interval()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync(
            "Heartbeat",
            interval: TimeSpan.FromMinutes(5),
            lastRunAt: host.Now - TimeSpan.FromSeconds(30),
            lastResult: "successful: Heartbeat OK");

        await host.StartAsync();

        var heartbeat = (await host.ReadJobsAsync()).Single(job => job.Name == "Heartbeat");

        heartbeat.NextRunAt.Should().Be(host.Now + TimeSpan.FromSeconds(30), "30 s ago plus one minute is 30 s away");
        heartbeat.LastRunAt.Should().Be(host.Now - TimeSpan.FromSeconds(30));
        heartbeat.LastResult.Should().Be("successful: Heartbeat OK");
        heartbeat.Interval.Should().Be(TimeSpan.FromMinutes(1), "the definition wins over the stored interval");
    }

    [Fact]
    public async Task A_run_that_is_overdue_schedules_shortly_after_start_and_keeps_the_last_run()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync(
            "CheckHealth",
            interval: TimeSpan.FromMinutes(15),
            lastRunAt: host.Now - TimeSpan.FromHours(1),
            lastResult: "unsuccessful: no indexer");

        await host.StartAsync();

        var checkHealth = (await host.ReadJobsAsync()).Single(job => job.Name == "CheckHealth");

        checkHealth.NextRunAt.Should().Be(host.Now + TimeSpan.FromSeconds(10), "an overdue task waits for the host to come up");
        checkHealth.LastRunAt.Should().Be(host.Now - TimeSpan.FromHours(1));
        checkHealth.LastResult.Should().Be("unsuccessful: no indexer");
    }

    [Fact]
    public async Task A_row_for_an_unknown_task_is_left_alone()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync(
            "SomethingRemoved",
            interval: TimeSpan.FromMinutes(3),
            lastRunAt: host.Now - TimeSpan.FromMinutes(6),
            lastResult: "successful: did work");

        await host.StartAsync();

        var jobs = await host.ReadJobsAsync();

        jobs.Should().HaveCount(3);
        var orphan = jobs.Single(job => job.Name == "SomethingRemoved");
        orphan.Interval.Should().Be(TimeSpan.FromMinutes(3));
        orphan.NextRunAt.Should().BeNull("rows for tasks the catalog does not know are not touched");
        orphan.LastResult.Should().Be("successful: did work");

        host.Triggers.Should().HaveCount(2, "no trigger is created for a name the catalog does not know");
    }

    /// <summary>
    /// The core container with the scheduler factory swapped out, so the service can be driven by hand.
    /// </summary>
    private sealed class TaskHost : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly ServiceProvider _provider;
        private readonly string _directory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly IScheduledTaskCatalog _catalog;

        private TaskHost(
            ServiceProvider provider,
            string directory,
            FakeTimeProvider timeProvider,
            ISchedulerFactory schedulerFactory,
            List<IJobDetail> jobs,
            List<ITrigger> triggers)
        {
            _provider = provider;
            _directory = directory;
            _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            _schedulerFactory = schedulerFactory;
            _catalog = provider.GetRequiredService<IScheduledTaskCatalog>();
            TimeProvider = timeProvider;
            Jobs = jobs;
            Triggers = triggers;
        }

        public FakeTimeProvider TimeProvider { get; }

        /// <summary>When the host started, as the service sees it.</summary>
        public DateTime Now => TimeProvider.GetUtcNow().UtcDateTime;

        /// <summary>The job details handed to the substituted scheduler.</summary>
        public List<IJobDetail> Jobs { get; }

        /// <summary>The triggers handed to the substituted scheduler.</summary>
        public List<ITrigger> Triggers { get; }

        public static async Task<TaskHost> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "compilarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "compilarr.db");
            var timeProvider = new FakeTimeProvider(Start);

            var jobs = new List<IJobDetail>();
            var triggers = new List<ITrigger>();

            var scheduler = Substitute.For<IScheduler>();

            // NSubstitute's setup syntax "calls" the ValueTask-returning members without consuming
            // the result, which CA2012 cannot tell apart from a real misuse.
#pragma warning disable CA2012
            scheduler
                .ScheduleJob(Arg.Any<IJobDetail>(), Arg.Any<ITrigger>(), Arg.Any<ScheduleJobOptions>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    jobs.Add(call.Arg<IJobDetail>());
                    triggers.Add(call.Arg<ITrigger>());

                    return new ValueTask<DateTimeOffset>(DateTimeOffset.UnixEpoch);
                });

            var factory = Substitute.For<ISchedulerFactory>();
            factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IScheduler>(scheduler));
#pragma warning restore CA2012

            var services = new ServiceCollection();
            services.AddLogging();

            // Registered before AddCompilarrCore, whose TryAddSingleton would otherwise win.
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddCompilarrPersistence($"Data Source={databasePath}");
            services.AddCompilarrCore();

            // Command handlers (BulkAddSongs) depend on the metadata services, as they do in the app.
            services.AddCompilarrMetadata(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
            }

            return new TaskHost(provider, directory, timeProvider, factory, jobs, triggers);
        }

        /// <summary>Runs the service's start-up as the host would, on the substituted scheduler.</summary>
        public Task StartAsync()
        {
            var service = new ScheduledTaskService(
                _schedulerFactory,
                _scopeFactory,
                _catalog,
                TimeProvider,
                NullLogger<ScheduledTaskService>.Instance);

            return service.StartAsync(CancellationToken.None);
        }

        /// <summary>Writes a job row directly, as a previous run and restart would leave it.</summary>
        public async Task InsertJobAsync(string name, TimeSpan? interval, DateTime? lastRunAt, string? lastResult)
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

            context.Jobs.Add(new Job
            {
                Name = name,
                Interval = interval,
                LastRunAt = lastRunAt,
                LastResult = lastResult,
            });

            await context.SaveChangesAsync(CancellationToken.None);
        }

        /// <summary>Reads the job table as the Tasks list would.</summary>
        public async Task<List<Job>> ReadJobsAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

            return await context.Jobs.AsNoTracking().OrderBy(job => job.Name).ToListAsync(CancellationToken.None);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            // Windows keeps the file handle until the pooled connections are gone.
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(_directory, "compilarr.db")}"));

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
