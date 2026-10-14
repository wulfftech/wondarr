namespace Wondarr.Core.Jobs;

/// <summary>
/// Scheduled tasks that belong to a project <c>Wondarr.Core</c> cannot see (the Soulseek share rescan
/// lives with slskd). The catalog adds every registered source's tasks to its own.
/// </summary>
public interface IScheduledTaskSource
{
    /// <summary>Gets this source's tasks, with the intervals its settings hold now.</summary>
    IReadOnlyList<ScheduledTaskDefinition> Tasks { get; }

    /// <summary>Calls <paramref name="listener"/> after a setting that decides one of the intervals changed.</summary>
    /// <param name="listener">Told that <see cref="Tasks"/> may differ.</param>
    /// <returns>Stops the notifications when disposed.</returns>
    IDisposable OnChange(Action listener);
}
