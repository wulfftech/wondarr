// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/Tasks/TaskController.cs, GPL-3.0.
// Adapted for Compilarr: the scheduled state lives in the job table rather than in a task manager, the
// interval is stored as a TimeSpan and reported in whole minutes, and lastDuration is a placeholder
// until the command table records a run's start time alongside its end.

using Compilarr.Api.Commands;
using Compilarr.Core.Jobs;
using Compilarr.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compilarr.Api.SystemInfo;

/// <summary>
/// The Tasks list dashboards and *arr clients read. "Run now" is not an endpoint here: it is
/// <c>POST /api/v1/command</c> with the task's <c>taskName</c>.
/// </summary>
[ApiController]
[Route("api/v1/system/task")]
public sealed class TaskController : ControllerBase
{
    /// <summary>What <c>lastDuration</c> reports until run times are recorded per task.</summary>
    public const string UnknownDuration = "00:00:00";

    private readonly CompilarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="TaskController"/> class.</summary>
    public TaskController(CompilarrDbContext context)
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

        return Ok(jobs.Select(ToResource).ToList());
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

        return job is null ? NotFound() : Ok(ToResource(job));
    }

    private static TaskResource ToResource(Job job) => new(
        job.Id,
        CommandResourceMapper.SplitCamelCase(job.Name),
        job.Name,
        IntervalMinutes(job.Interval),
        job.LastRunAt,
        job.LastRunAt,
        job.NextRunAt,
        UnknownDuration,
        job.LastResult);

    private static int IntervalMinutes(TimeSpan? interval) =>
        interval is { } value ? (int)Math.Round(value.TotalMinutes) : 0;
}
