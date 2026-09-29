using Microsoft.Extensions.Options;
using Wondarr.Core.Searching;

namespace Wondarr.Core.Jobs;

/// <summary>
/// The tasks ARCHITECTURE §5.5 schedules from a cold start. Only the two Phase 0 tasks exist so far;
/// the rest of the table in that section lands with the features they belong to.
/// </summary>
public sealed class ScheduledTaskCatalog : IScheduledTaskCatalog
{
    /// <summary>How often the heartbeat runs.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the health checks run.</summary>
    public static readonly TimeSpan CheckHealthInterval = TimeSpan.FromMinutes(15);

    /// <summary>How often the missing-song search runs, when <c>search.missing_interval_hours</c> is left alone.</summary>
    public static readonly TimeSpan DefaultMissingSearchInterval = TimeSpan.FromHours(6);

    private readonly IReadOnlyList<ScheduledTaskDefinition> _tasks;

    /// <summary>Initialises a new instance of the <see cref="ScheduledTaskCatalog"/> class.</summary>
    /// <param name="options">The search settings, which decide how often the missing-song task runs.</param>
    public ScheduledTaskCatalog(IOptions<SearchOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _tasks =
        [
            new(HeartbeatCommandHandler.CommandName, HeartbeatInterval),
            new(CheckHealthCommandHandler.CommandName, CheckHealthInterval),
            new(
                MissingSearchCommandHandler.CommandName,
                TimeSpan.FromHours(options.Value.MissingIntervalHours)),
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDefinition> Tasks => _tasks;

    /// <inheritdoc />
    public ScheduledTaskDefinition? Find(string commandName) =>
        _tasks.FirstOrDefault(definition =>
            string.Equals(definition.CommandName, commandName, StringComparison.OrdinalIgnoreCase));
}
