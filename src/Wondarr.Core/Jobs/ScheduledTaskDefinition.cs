namespace Wondarr.Core.Jobs;

/// <summary>
/// One recurring task the scheduler runs: the command to enqueue and how often to enqueue it.
/// The interval is the only schedule Wondarr has so far (no cron expressions yet).
/// </summary>
/// <param name="CommandName">The command name, as <see cref="ICommandHandler.Name"/> reports it.</param>
/// <param name="Interval">How long to wait between runs.</param>
public sealed record ScheduledTaskDefinition(string CommandName, TimeSpan Interval);
