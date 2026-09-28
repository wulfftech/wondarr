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

    private static readonly ScheduledTaskDefinition[] BuiltIn =
    [
        new(HeartbeatCommandHandler.CommandName, HeartbeatInterval),
        new(CheckHealthCommandHandler.CommandName, CheckHealthInterval),
    ];

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDefinition> Tasks => BuiltIn;

    /// <inheritdoc />
    public ScheduledTaskDefinition? Find(string commandName) =>
        BuiltIn.FirstOrDefault(definition =>
            string.Equals(definition.CommandName, commandName, StringComparison.OrdinalIgnoreCase));
}
