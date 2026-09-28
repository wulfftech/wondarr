using System.Diagnostics.CodeAnalysis;

namespace Compilarr.Core.Jobs;

/// <summary>
/// The queue in front of the command executor: everything that wants a command to run goes through
/// here, and the queue is the only writer of the <c>command</c> table's identity columns.
/// </summary>
// The name is fixed by the task spec (it is the *arr vocabulary) and is not a System.Collections.Queue.
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the task spec; this is a command queue, not a collection.")]
public interface ICommandQueue
{
    /// <summary>
    /// Queues a command, or returns the command already queued or running under the same name.
    /// </summary>
    /// <param name="name">The command name, for example <c>Heartbeat</c>.</param>
    /// <param name="body">The raw JSON body the caller sent, or <see langword="null"/>.</param>
    /// <param name="trigger">What asked for the command.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    /// <returns>The queued command.</returns>
    /// <exception cref="UnknownCommandException">No handler answers to <paramref name="name"/>.</exception>
    Task<CommandRecord> EnqueueAsync(
        string name,
        string? body,
        CommandTrigger trigger,
        CancellationToken cancellationToken);

    /// <summary>Reads one command, or <see langword="null"/> when it does not exist.</summary>
    Task<CommandRecord?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Reads the most recently queued commands, newest first.</summary>
    /// <param name="take">How many rows to return at most.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<CommandRecord>> ListAsync(int take = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a command that has not started yet. A command that is already running (or finished)
    /// cannot be cancelled this way.
    /// </summary>
    /// <param name="id">The command row.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="true"/> when the command was queued and is now cancelled.</returns>
    Task<bool> CancelAsync(long id, CancellationToken cancellationToken);
}
