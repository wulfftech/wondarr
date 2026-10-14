using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Recreates the schedules after every start. The <c>job</c> table is the state that survives a
/// restart and Quartz is the clock, so this runs once at startup: it upserts a row per catalog
/// definition, works out when each task is next due and hands Quartz a repeating trigger for it.
/// <para>
/// Registered after the migration hosted service (the <c>job</c> table must exist) and before the
/// Quartz hosted service, which starts the scheduler these triggers are registered on.
/// </para>
/// </summary>
public sealed partial class ScheduledTaskService : IHostedService, IDisposable
{
    /// <summary>
    /// How long after startup a task runs when its stored schedule says it is already overdue. Not
    /// zero: the rest of the host should be up before a task starts doing work.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScheduledTaskCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledTaskService> _logger;
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private readonly Lock _applyingLock = new();
    private Task _applying = Task.CompletedTask;
    private IDisposable? _changeSubscription;
    private int _disposed;

    /// <summary>Initialises a new instance of the <see cref="ScheduledTaskService"/> class.</summary>
    public ScheduledTaskService(
        ISchedulerFactory schedulerFactory,
        IServiceScopeFactory scopeFactory,
        IScheduledTaskCatalog catalog,
        TimeProvider timeProvider,
        ILogger<ScheduledTaskService> logger)
    {
        ArgumentNullException.ThrowIfNull(schedulerFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _schedulerFactory = schedulerFactory;
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Listening starts before the first task is scheduled, so a setting that changes while the loop
        // runs is not missed; the change is applied once the gate is free, after the loop has finished.
        _changeSubscription = _catalog.OnChange(OnIntervalSettingChanged);

        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ScheduleAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    private async Task ScheduleAllAsync(CancellationToken cancellationToken)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var definition in _catalog.Tasks)
        {
            var nextRunAt = await UpsertJobAsync(definition, now, cancellationToken).ConfigureAwait(false);

            // StartAt takes the first run, the simple schedule repeats it every Interval — so Quartz
            // and the job row agree until a run finishes, when JobTableUpdater moves NextRunAt on.
            var detail = JobBuilder.Create<EnqueueCommandJob>()
                .WithIdentity(definition.CommandName)
                .UsingJobData(EnqueueCommandJob.CommandNameKey, definition.CommandName)
                .Build();

            var trigger = TriggerBuilder.Create()
                .ForJob(detail)
                .WithIdentity(TriggerKeyFor(definition.CommandName))
                .StartAt(new DateTimeOffset(nextRunAt, TimeSpan.Zero))
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(definition.Interval)
                    .RepeatForever())
                .Build();

            await scheduler.ScheduleJob(detail, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);

            LogScheduled(definition.CommandName, definition.Interval, nextRunAt);
        }
    }

    /// <summary>
    /// Moves a task whose interval setting changed: Quartz gets a trigger with the new interval and a next
    /// run of last run plus that interval, never earlier than now (so the longest overdue task runs at
    /// once), and only then does the row take the same values. If Quartz refuses, the row still has the old
    /// interval, so the next change (or restart) tries again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the update.</param>
    private async Task ApplyIntervalChangesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var definition in _catalog.Tasks)
            {
                if (definition.Interval <= TimeSpan.Zero)
                {
                    // A setting reloaded to something the validator would refuse: keep the schedule that works.
                    continue;
                }

                await MoveJobAsync(scheduler, definition, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _changeGate.Release();
        }
    }

    /// <summary>
    /// Gets a task that ends when every interval change heard so far has been applied, so a test can wait
    /// for the work the configuration reload's callback starts.
    /// </summary>
    internal Task ChangesApplied
    {
        get
        {
            lock (_applyingLock)
            {
                return _applying;
            }
        }
    }

    private void OnIntervalSettingChanged()
    {
        // Called on the configuration reload's thread: nothing may escape it, and it does not wait.
        var work = Task.Run(async () =>
        {
            try
            {
                await ApplyIntervalChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogRescheduleFailed(exception);
            }
        });

        lock (_applyingLock)
        {
            _applying = Task.WhenAll(_applying, work);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changeSubscription?.Dispose();

        // An apply that is already running finishes before the gate goes; a later one sees _disposed.
        try
        {
            ChangesApplied.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The apply logged its own failure.
        }

        _changeGate.Dispose();
    }

    /// <summary>The key of the trigger that fires a task, so its next fire time can be read back.</summary>
    /// <param name="commandName">The task's command name.</param>
    internal static TriggerKey TriggerKeyFor(string commandName) => new($"{commandName}.trigger");

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The Quartz hosted service owns the scheduler's lifetime; only the settings subscription is ours.
        _changeSubscription?.Dispose();
        _changeSubscription = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the row for one definition and returns when that task is next due.
    /// <paramref name="now"/> is the moment the host started.
    /// </summary>
    internal static DateTime NextRunAt(DateTime? lastRunAt, TimeSpan interval, DateTime now, TimeSpan? firstRunDelay = null)
    {
        if (lastRunAt is { } lastRun)
        {
            var due = lastRun + interval;

            if (due > now)
            {
                return due;
            }
        }

        return now + (firstRunDelay ?? StartupDelay);
    }

    private async Task<DateTime> UpsertJobAsync(
        ScheduledTaskDefinition definition,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var job = await context.Jobs
            .FirstOrDefaultAsync(row => row.Name == definition.CommandName, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            job = new Job { Name = definition.CommandName };
            context.Jobs.Add(job);
        }

        // The definition wins on the interval, so changing it in code takes effect on restart; the
        // run history is left exactly as the last run wrote it.
        job.Interval = definition.Interval;
        job.NextRunAt = NextRunAt(job.LastRunAt, definition.Interval, now, definition.FirstRunDelay);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return job.NextRunAt!.Value;
    }

    /// <summary>
    /// Moves one task whose row has another interval than its definition: the trigger first, then the row.
    /// Does nothing when the row already has this interval (or there is no row yet).
    /// </summary>
    private async Task MoveJobAsync(
        IScheduler scheduler,
        ScheduledTaskDefinition definition,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var job = await context.Jobs
            .FirstOrDefaultAsync(row => row.Name == definition.CommandName, cancellationToken)
            .ConfigureAwait(false);

        if (job is null || job.Interval == definition.Interval)
        {
            return;
        }

        // A task that has run is due its last run plus the new interval; one that never has keeps the
        // time it was waiting for. Neither is ever in the past: the most a change can do is run it now.
        var due = job.LastRunAt is { } lastRun ? lastRun + definition.Interval : job.NextRunAt ?? now;
        var nextRunAt = due > now ? due : now;

        var trigger = TriggerBuilder.Create()
            .ForJob(definition.CommandName)
            .WithIdentity(TriggerKeyFor(definition.CommandName))
            .StartAt(new DateTimeOffset(nextRunAt, TimeSpan.Zero))
            .WithSimpleSchedule(schedule => schedule
                .WithInterval(definition.Interval)
                .RepeatForever())
            .Build();

        await scheduler
            .RescheduleJob(TriggerKeyFor(definition.CommandName), trigger, cancellationToken)
            .ConfigureAwait(false);

        job.Interval = definition.Interval;
        job.NextRunAt = nextRunAt;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRescheduled(definition.CommandName, definition.Interval, nextRunAt);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Scheduled {CommandName} every {Interval} starting at {NextRunAt:o}")]
    private partial void LogScheduled(string commandName, TimeSpan interval, DateTime nextRunAt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The interval of {CommandName} changed: now every {Interval}, next run {NextRunAt:o}")]
    private partial void LogRescheduled(string commandName, TimeSpan interval, DateTime nextRunAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A changed task interval could not be applied; it takes effect at the next start")]
    private partial void LogRescheduleFailed(Exception exception);
}
