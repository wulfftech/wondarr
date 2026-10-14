using Wondarr.Core.Metadata;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quartz;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

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

        jobs.Select(job => job.Name).Should().BeEquivalentTo("Heartbeat", "CheckHealth", "MissingSearch", "UpgradeSearch", "ReferenceLibraryScan", "Backup", "ImportListSync", "Housekeeping", "CheckForUpdates");
        jobs.Where(job => job.Name != "CheckForUpdates").Should().OnlyContain(job => job.NextRunAt == host.Now + TimeSpan.FromSeconds(10));
        jobs.Single(job => job.Name == "CheckForUpdates").NextRunAt.Should().Be(host.Now + TimeSpan.FromMinutes(2), "the update check waits longer than the other tasks on a first run");
        jobs.Single(job => job.Name == "CheckForUpdates").Interval.Should().Be(TimeSpan.FromHours(12));
        jobs.Single(job => job.Name == "Heartbeat").Interval.Should().Be(TimeSpan.FromMinutes(1));
        jobs.Single(job => job.Name == "CheckHealth").Interval.Should().Be(TimeSpan.FromMinutes(15));
        jobs.Single(job => job.Name == "MissingSearch").Interval.Should().Be(TimeSpan.FromHours(6));
        jobs.Single(job => job.Name == "Backup").Interval.Should().Be(TimeSpan.FromDays(7));
        jobs.Single(job => job.Name == "UpgradeSearch").Interval.Should().Be(TimeSpan.FromHours(24));
        jobs.Should().OnlyContain(job => job.LastRunAt == null && job.LastResult == null);

        host.Triggers.Should().HaveCount(jobs.Count, "every row gets its own trigger");
        host.Triggers.Select(trigger => trigger.JobKey.Name)
            .Should().BeEquivalentTo("Heartbeat", "CheckHealth", "MissingSearch", "UpgradeSearch", "ReferenceLibraryScan", "Backup", "ImportListSync", "Housekeeping", "CheckForUpdates");
        host.Triggers.Where(trigger => trigger.JobKey.Name != "CheckForUpdates").Should().OnlyContain(trigger =>
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

        jobs.Should().HaveCount(10);
        var orphan = jobs.Single(job => job.Name == "SomethingRemoved");
        orphan.Interval.Should().Be(TimeSpan.FromMinutes(3));
        orphan.NextRunAt.Should().BeNull("rows for tasks the catalog does not know are not touched");
        orphan.LastResult.Should().Be("successful: did work");

        host.Triggers.Should().HaveCount(9, "no trigger is created for a name the catalog does not know");
    }

    [Fact]
    public async Task A_changed_interval_moves_the_row_and_the_trigger_to_last_run_plus_the_new_interval()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync("MissingSearch", TimeSpan.FromHours(6), host.Now - TimeSpan.FromHours(1), "successful: 3 songs");
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 3 });
        await service.ChangesApplied;

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch");
        row.Interval.Should().Be(TimeSpan.FromHours(3));
        row.NextRunAt.Should().Be(host.Now + TimeSpan.FromHours(2), "an hour ago plus three hours");
        row.LastRunAt.Should().Be(host.Now - TimeSpan.FromHours(1), "the run history is left alone");

        var trigger = host.Rescheduled.Should().ContainSingle().Which;
        trigger.Key.Name.Should().Be("MissingSearch.trigger");
        trigger.StartTimeUtc.Should().Be(new DateTimeOffset(host.Now + TimeSpan.FromHours(2), TimeSpan.Zero));
        ((ISimpleTrigger)trigger).RepeatInterval.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task A_new_interval_shorter_than_the_time_since_the_last_run_runs_the_task_now_not_in_the_past()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync("MissingSearch", TimeSpan.FromHours(6), host.Now - TimeSpan.FromHours(5), "successful: 3 songs");
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 1 });
        await service.ChangesApplied;

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch");
        row.Interval.Should().Be(TimeSpan.FromHours(1));
        row.NextRunAt.Should().Be(host.Now, "five hours ago plus one hour is in the past, so the most it can do is now");
        host.Rescheduled.Should().ContainSingle().Which.StartTimeUtc
            .Should().Be(new DateTimeOffset(host.Now, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_longer_interval_pushes_the_next_run_out()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync("UpgradeSearch", TimeSpan.FromHours(24), host.Now - TimeSpan.FromHours(10), "successful: 0 songs");
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { UpgradeIntervalHours = 72 });
        await service.ChangesApplied;

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "UpgradeSearch");
        row.NextRunAt.Should().Be(host.Now + TimeSpan.FromHours(62));
    }

    [Fact]
    public async Task A_task_that_never_ran_keeps_the_time_it_was_waiting_for()
    {
        await using var host = await TaskHost.CreateAsync();
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 12 });
        await service.ChangesApplied;

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch");
        row.Interval.Should().Be(TimeSpan.FromHours(12));
        row.NextRunAt.Should().Be(host.Now + ScheduledTaskService.StartupDelay);
    }

    [Fact]
    public async Task A_setting_that_changed_nothing_about_the_intervals_moves_no_task()
    {
        await using var host = await TaskHost.CreateAsync();
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingBatchSize = 10 });
        await service.ChangesApplied;

        host.Rescheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reloaded_setting_the_validator_would_refuse_leaves_the_working_schedule_alone()
    {
        await using var host = await TaskHost.CreateAsync();
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 0 });
        await service.ChangesApplied;

        host.Rescheduled.Should().BeEmpty();
        (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch").Interval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task A_changed_interval_reschedules_the_trigger_on_a_real_scheduler()
    {
        await using var host = await TaskHost.CreateAsync(realScheduler: true);
        await host.InsertJobAsync("MissingSearch", TimeSpan.FromHours(6), host.Now - TimeSpan.FromHours(1), "successful: 3 songs");
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);
        var scheduler = await host.SchedulerFactory.GetScheduler();
        var key = ScheduledTaskService.TriggerKeyFor("MissingSearch");

        var before = (ISimpleTrigger)(await scheduler.GetTrigger(key))!;
        before.RepeatInterval.Should().Be(TimeSpan.FromHours(6));
        before.NextFireTimeUtc.Should().Be(new DateTimeOffset(host.Now + TimeSpan.FromHours(5), TimeSpan.Zero));

        host.Search.Set(new SearchOptions { MissingIntervalHours = 3 });
        await service.ChangesApplied;

        var after = (ISimpleTrigger)(await scheduler.GetTrigger(key))!;
        after.RepeatInterval.Should().Be(TimeSpan.FromHours(3), "Quartz now fires on the new interval");
        after.NextFireTimeUtc.Should().Be(new DateTimeOffset(host.Now + TimeSpan.FromHours(2), TimeSpan.Zero));
        (await scheduler.GetJobDetail(new JobKey("MissingSearch"))).Should().NotBeNull("rescheduling keeps the job");

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch");
        row.Interval.Should().Be(TimeSpan.FromHours(3));
        row.NextRunAt.Should().Be(host.Now + TimeSpan.FromHours(2), "the row agrees with the trigger");
    }

    [Fact]
    public async Task The_row_keeps_its_old_interval_when_the_scheduler_refuses_the_new_trigger()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync("MissingSearch", TimeSpan.FromHours(6), host.Now - TimeSpan.FromHours(1), "successful: 3 songs");
        host.FailReschedule = true;
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 3 });
        await service.ChangesApplied;

        var row = (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch");
        row.Interval.Should().Be(TimeSpan.FromHours(6), "a row that already says 3 h would hide that Quartz never moved");
        row.NextRunAt.Should().Be(host.Now + TimeSpan.FromHours(5));

        // The next change (or restart) finds the row differing from the definition and tries again.
        host.FailReschedule = false;
        host.Search.Set(new SearchOptions { MissingIntervalHours = 3 });
        await service.ChangesApplied;

        (await host.ReadJobsAsync()).Single(job => job.Name == "MissingSearch").Interval.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task Disposing_the_service_waits_for_a_change_that_is_being_applied()
    {
        await using var host = await TaskHost.CreateAsync();
        await host.InsertJobAsync("MissingSearch", TimeSpan.FromHours(6), host.Now - TimeSpan.FromHours(1), "successful: 3 songs");
        var service = host.CreateService();
        await service.StartAsync(CancellationToken.None);

        host.Search.Set(new SearchOptions { MissingIntervalHours = 3 });
        service.Dispose();

        service.ChangesApplied.IsCompleted.Should().BeTrue("Dispose does not return before the apply it found running");

        // A change after the service is gone is ignored instead of touching the disposed gate.
        host.Search.Set(new SearchOptions { MissingIntervalHours = 2 });
        service.Dispose();
    }

    [Fact]
    public async Task The_service_listens_for_setting_changes_from_start_to_stop()
    {
        await using var host = await TaskHost.CreateAsync();
        var service = host.CreateService();

        await service.StartAsync(CancellationToken.None);
        host.Search.ListenerCount.Should().Be(1);

        await service.StopAsync(CancellationToken.None);
        host.Search.ListenerCount.Should().Be(0);
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
            List<ITrigger> triggers,
            List<ITrigger> rescheduled,
            MutableOptionsMonitor<SearchOptions> search)
        {
            _provider = provider;
            _directory = directory;
            _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            _schedulerFactory = schedulerFactory;
            _catalog = provider.GetRequiredService<IScheduledTaskCatalog>();
            TimeProvider = timeProvider;
            Jobs = jobs;
            Triggers = triggers;
            Rescheduled = rescheduled;
            Search = search;
        }

        /// <summary>Whether the substituted scheduler refuses a reschedule.</summary>
        public bool FailReschedule
        {
            get => FailRescheduleBox!.Value;
            set => FailRescheduleBox!.Value = value;
        }

        /// <summary>The scheduler factory the service runs on.</summary>
        public ISchedulerFactory SchedulerFactory => _schedulerFactory;

        private StrongBox<bool>? FailRescheduleBox { get; init; }

        /// <summary>The search settings, which a test changes the way a reload would.</summary>
        public MutableOptionsMonitor<SearchOptions> Search { get; }

        /// <summary>The triggers the substituted scheduler was told to put in place of the running ones.</summary>
        public List<ITrigger> Rescheduled { get; }

        public FakeTimeProvider TimeProvider { get; }

        /// <summary>When the host started, as the service sees it.</summary>
        public DateTime Now => TimeProvider.GetUtcNow().UtcDateTime;

        /// <summary>The job details handed to the substituted scheduler.</summary>
        public List<IJobDetail> Jobs { get; }

        /// <summary>The triggers handed to the substituted scheduler.</summary>
        public List<ITrigger> Triggers { get; }

        /// <param name="realScheduler">
        /// Whether the scheduler is a real Quartz one on its in-memory job store (never started, so nothing
        /// fires) instead of a substitute that records what it was told.
        /// </param>
        public static async Task<TaskHost> CreateAsync(bool realScheduler = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "wondarr.db");
            var timeProvider = new FakeTimeProvider(Start);

            var jobs = new List<IJobDetail>();
            var triggers = new List<ITrigger>();
            var rescheduled = new List<ITrigger>();
            var search = new MutableOptionsMonitor<SearchOptions>(new SearchOptions());

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

            var failReschedule = new StrongBox<bool>();

            scheduler
                .RescheduleJob(Arg.Any<TriggerKey>(), Arg.Any<ITrigger>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (failReschedule.Value)
                    {
                        throw new SchedulerException("the scheduler refused the trigger");
                    }

                    rescheduled.Add(call.Arg<ITrigger>());

                    return new ValueTask<DateTimeOffset?>(DateTimeOffset.UnixEpoch);
                });

            var factory = Substitute.For<ISchedulerFactory>();
            factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IScheduler>(scheduler));
#pragma warning restore CA2012

            var services = new ServiceCollection();
            services.AddLogging();

            // Registered before AddWondarrCore, whose TryAddSingleton would otherwise win.
            services.AddSingleton<TimeProvider>(timeProvider);

            // A search-settings monitor the test can change, as a configuration reload would.
            services.AddSingleton<IOptionsMonitor<SearchOptions>>(search);
            services.AddWondarrPersistence($"Data Source={databasePath}");
            services.AddWondarrCore();

            // Command handlers (BulkAddSongs) depend on the metadata services, as they do in the app.
            services.AddWondarrMetadata(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
            }

            // The real scheduler is the one AddWondarrCore registers (in-memory job store); it is never
            // started, so no trigger fires.
            return new TaskHost(provider, directory, timeProvider, realScheduler ? provider.GetRequiredService<ISchedulerFactory>() : factory, jobs, triggers, rescheduled, search)
            {
                FailRescheduleBox = failReschedule,
            };
        }

        /// <summary>Builds the service on the substituted scheduler.</summary>
        public ScheduledTaskService CreateService() =>
            new(
                _schedulerFactory,
                _scopeFactory,
                _catalog,
                TimeProvider,
                NullLogger<ScheduledTaskService>.Instance);

        /// <summary>Runs the service's start-up as the host would, on the substituted scheduler.</summary>
        public Task StartAsync() => CreateService().StartAsync(CancellationToken.None);

        /// <summary>Writes a job row directly, as a previous run and restart would leave it.</summary>
        public async Task InsertJobAsync(string name, TimeSpan? interval, DateTime? lastRunAt, string? lastResult)
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

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
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            return await context.Jobs.AsNoTracking().OrderBy(job => job.Name).ToListAsync(CancellationToken.None);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            // Windows keeps the file handle until the pooled connections are gone.
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(_directory, "wondarr.db")}"));

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
