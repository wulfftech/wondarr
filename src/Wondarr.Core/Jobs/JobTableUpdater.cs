using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Writes the outcome of a command back to its <c>job</c> row, so the Tasks list shows when a task
/// last ran whether it was scheduled or asked for by hand — "run now" is just
/// <c>POST /api/v1/command</c>.
/// <para>
/// A command with no job row (anything that is not a scheduled task) is ignored: this handler follows
/// the schedule, it does not create one.
/// </para>
/// </summary>
public sealed class JobTableUpdater : IHandle<CommandUpdatedEvent>
{
    /// <summary>How much of the outcome the <c>job</c> table keeps.</summary>
    public const int MaxResultLength = 500;

    private readonly WondarrDbContext _context;
    private readonly ISchedulerFactory? _schedulerFactory;

    /// <summary>Initialises a new instance of the <see cref="JobTableUpdater"/> class.</summary>
    /// <param name="context">The database the <c>job</c> row lives in.</param>
    /// <param name="schedulerFactory">
    /// The scheduler whose trigger says when the task is next due; without one the next run is the end of
    /// this run plus the interval.
    /// </param>
    public JobTableUpdater(WondarrDbContext context, ISchedulerFactory? schedulerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _schedulerFactory = schedulerFactory;
    }

    /// <inheritdoc />
    public async Task HandleAsync(CommandUpdatedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var command = message.Command;

        // Only a finished run says anything about the schedule; queued, started and cancelled rows
        // leave the previous run's numbers in place.
        if (command.Status is not (CommandStatus.Completed or CommandStatus.Failed)
            || command.EndedAt is not { } endedAt)
        {
            return;
        }

        var job = await _context.Jobs
            .FirstOrDefaultAsync(row => row.Name == command.Name, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            return;
        }

        job.LastRunAt = endedAt;
        job.NextRunAt = await NextRunAtAsync(command.Name, job.Interval, endedAt, cancellationToken).ConfigureAwait(false);
        job.LastResult = Describe(command);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// When the task is next due. Quartz fires on its own schedule whatever a run's end is, and a "run
    /// now" does not move it, so the trigger's next fire time is the answer; the end of the run plus the
    /// interval is only the fallback for a task with no trigger (R-071).
    /// </summary>
    private async Task<DateTime?> NextRunAtAsync(
        string name,
        TimeSpan? interval,
        DateTime endedAt,
        CancellationToken cancellationToken)
    {
        if (_schedulerFactory is not null)
        {
            try
            {
                var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
                var trigger = await scheduler
                    .GetTrigger(ScheduledTaskService.TriggerKeyFor(name), cancellationToken)
                    .ConfigureAwait(false);

                if (trigger?.NextFireTimeUtc is { } next)
                {
                    return next.UtcDateTime;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The schedule is the clock, the row only a copy of it: fall back to the interval.
            }
        }

        return interval is { } period ? endedAt + period : null;
    }

    /// <summary>Renders <c>"&lt;result&gt;: &lt;message or exception&gt;"</c>, trimmed to what the table keeps.</summary>
    internal static string Describe(CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var successful = command.Result == CommandResult.Successful;
        var detail = successful
            ? command.Message ?? command.Exception
            : command.Exception ?? command.Message;

        var result = successful ? "successful" : "unsuccessful";
        var text = string.IsNullOrWhiteSpace(detail) ? result : $"{result}: {detail}";

        return text.Length <= MaxResultLength ? text : text[..MaxResultLength];
    }
}
