namespace Wondarr.Core.Jobs;

/// <summary>
/// The recurring tasks this build knows about. The <c>job</c> table holds their state; this holds
/// their definitions, so a restart re-creates the rows without losing what the last run recorded.
/// </summary>
public interface IScheduledTaskCatalog
{
    /// <summary>Gets the built-in tasks, in a stable order.</summary>
    IReadOnlyList<ScheduledTaskDefinition> Tasks { get; }

    /// <summary>Finds the definition for <paramref name="commandName"/>, or <see langword="null"/>.</summary>
    /// <param name="commandName">The command name to look up.</param>
    ScheduledTaskDefinition? Find(string commandName);
}
