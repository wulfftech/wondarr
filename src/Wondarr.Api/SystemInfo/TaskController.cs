// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/Tasks/TaskController.cs, GPL-3.0.
// Adapted for Wondarr: the scheduled state lives in the job table rather than in a task manager, the
// interval is stored as a TimeSpan and reported in whole minutes, and the run times come from the
// command table, which records a run's start alongside its end.

using System.Globalization;
using Wondarr.Api.Commands;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Api.SystemInfo;

/// <summary>
/// The Tasks list dashboards and *arr clients read. "Run now" is not an endpoint here: it is
/// <c>POST /api/v1/command</c> with the task's <c>taskName</c>.
/// </summary>
[ApiController]
[Route("api/v1/system/task")]
public sealed class TaskController : ControllerBase
{
    /// <summary>What <c>lastDuration</c> reports for a task that has never run.</summary>
    public const string UnknownDuration = "00:00:00";

    private readonly WondarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="TaskController"/> class.</summary>
    public TaskController(WondarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <summary>Lists the scheduled tasks, by name.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<TaskResource>>> GetTasks(CancellationToken cancellationToken)
    {
        var jobs = await _context.Jobs
            .AsNoTracking()
            .OrderBy(job => job.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var runTimes = await LatestRunTimesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(jobs
            .Select(job => ToResource(job, runTimes.TryGetValue(job.Name, out var runTime) ? runTime : null))
            .ToList());
    }

    /// <summary>Reads one scheduled task.</summary>
    /// <param name="id">The <c>job</c> row.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<TaskResource>> GetTask(long id, CancellationToken cancellationToken)
    {
        var job = await _context.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            return NotFound();
        }

        var runTimes = await LatestRunTimesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(ToResource(job, runTimes.TryGetValue(job.Name, out var runTime) ? runTime : null));
    }

    /// <summary>
    /// The most recent finished command per name, one row per name. The executor runs up to three
    /// commands at a time, so the newest end time and the newest start time could come from different
    /// rows; taking whole rows keeps them paired. Only one row per name leaves the database: the
    /// command table is never pruned, and the heartbeat alone adds one row a minute.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, TaskRunTime>> LatestRunTimesAsync(CancellationToken cancellationToken)
    {
        var finished = await _context.Commands
            .AsNoTracking()
            .Where(command => command.StartedAt != null && command.EndedAt != null)
            .GroupBy(command => command.Name)
            .Select(group => group
                .OrderByDescending(command => command.EndedAt)
                .ThenByDescending(command => command.Id)
                .Select(command => new { command.Name, command.StartedAt, command.EndedAt })
                .First())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latest = new Dictionary<string, TaskRunTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in finished.OrderBy(command => command.EndedAt))
        {
            latest[command.Name] = new TaskRunTime(command.StartedAt!.Value, command.EndedAt!.Value);
        }

        return latest;
    }

    private static TaskResource ToResource(Job job, TaskRunTime? runTime) => new(
        job.Id,
        CommandResourceMapper.SplitCamelCase(job.Name),
        job.Name,
        IntervalMinutes(job.Interval),
        job.LastRunAt,
        runTime?.StartedAt ?? job.LastRunAt,
        job.NextRunAt,
        runTime is null ? UnknownDuration : FormatDuration(runTime.StartedAt, runTime.EndedAt),
        job.LastResult);

    private static int IntervalMinutes(TimeSpan? interval) =>
        interval is { } value ? (int)Math.Round(value.TotalMinutes) : 0;

    /// <summary>Whole seconds, the way the *arrs show a task's run time.</summary>
    private static string FormatDuration(DateTime startedAt, DateTime endedAt)
    {
        var duration = endedAt - startedAt;

        // A clock that ran backwards between the two stamps reports no time at all.
        return duration < TimeSpan.Zero
            ? UnknownDuration
            : duration.ToString("c", CultureInfo.InvariantCulture);
    }
}

/// <summary>When a task's last run started and ended, from the command table.</summary>
/// <param name="StartedAt">The UTC instant the command was picked up.</param>
/// <param name="EndedAt">The UTC instant it reached a terminal state.</param>
internal sealed record TaskRunTime(DateTime StartedAt, DateTime EndedAt);
