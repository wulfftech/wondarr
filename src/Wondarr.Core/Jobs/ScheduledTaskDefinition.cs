namespace Wondarr.Core.Jobs;

/// <summary>
/// One recurring task the scheduler runs: the command to enqueue and how often to enqueue it.
/// The interval is the only schedule Wondarr has so far (no cron expressions yet).
/// </summary>
/// <param name="CommandName">The command name, as <see cref="ICommandHandler.Name"/> reports it.</param>
/// <param name="Interval">How long to wait between runs.</param>
/// <param name="FirstRunDelay">
/// How long after startup the task first runs when it is due or has never run; <see langword="null"/>
/// for <see cref="ScheduledTaskService.StartupDelay"/>. A task that last ran recently still waits for its interval.
/// </param>
public sealed record ScheduledTaskDefinition(string CommandName, TimeSpan Interval, TimeSpan? FirstRunDelay = null);
