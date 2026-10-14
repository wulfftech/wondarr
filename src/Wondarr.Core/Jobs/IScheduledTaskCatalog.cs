namespace Wondarr.Core.Jobs;

/// <summary>
/// The recurring tasks this build knows about. The <c>job</c> table holds their state; this holds
/// their definitions, so a restart re-creates the rows without losing what the last run recorded.
/// </summary>
public interface IScheduledTaskCatalog
{
    /// <summary>
    /// Gets the built-in tasks, in a stable order. The intervals are the ones the settings hold now, so
    /// a change to a setting shows here without a restart.
    /// </summary>
    IReadOnlyList<ScheduledTaskDefinition> Tasks { get; }

    /// <summary>Calls <paramref name="listener"/> after a setting that decides a task's interval changed.</summary>
    /// <param name="listener">Told that the definitions may differ; it reads <see cref="Tasks"/> again.</param>
    /// <returns>Stops the notifications when disposed.</returns>
    IDisposable OnChange(Action listener);

    /// <summary>Finds the definition for <paramref name="commandName"/>, or <see langword="null"/>.</summary>
    /// <param name="commandName">The command name to look up.</param>
    ScheduledTaskDefinition? Find(string commandName);
}
