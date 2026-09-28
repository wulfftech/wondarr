using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Compilarr.Core.Jobs;

/// <summary>
/// Recreates the schedules after every start. The <c>job</c> table is the state that survives a
/// restart and Quartz is the clock, so this runs once at startup: it upserts a row per catalog
/// definition, works out when each task is next due and hands Quartz a repeating trigger for it.
/// <para>
/// Registered after the migration hosted service (the <c>job</c> table must exist) and before the
/// Quartz hosted service, which starts the scheduler these triggers are registered on.
/// </para>
/// </summary>
public sealed partial class ScheduledTaskService : IHostedService
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
                .WithIdentity($"{definition.CommandName}.trigger")
                .StartAt(new DateTimeOffset(nextRunAt, TimeSpan.Zero))
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(definition.Interval)
                    .RepeatForever())
                .Build();

            await scheduler.ScheduleJob(detail, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);

            LogScheduled(definition.CommandName, definition.Interval, nextRunAt);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The Quartz hosted service owns the scheduler's lifetime; there is nothing of ours to stop.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the row for one definition and returns when that task is next due.
    /// <paramref name="now"/> is the moment the host started.
    /// </summary>
    internal static DateTime NextRunAt(DateTime? lastRunAt, TimeSpan interval, DateTime now)
    {
        if (lastRunAt is { } lastRun)
        {
            var due = lastRun + interval;

            if (due > now)
            {
                return due;
            }
        }

        return now + StartupDelay;
    }

    private async Task<DateTime> UpsertJobAsync(
        ScheduledTaskDefinition definition,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

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
        job.NextRunAt = NextRunAt(job.LastRunAt, definition.Interval, now);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return job.NextRunAt!.Value;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Scheduled {CommandName} every {Interval} starting at {NextRunAt:o}")]
    private partial void LogScheduled(string commandName, TimeSpan interval, DateTime nextRunAt);
}
