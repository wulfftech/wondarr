using System.Collections.Concurrent;

namespace Wondarr.Core.Jobs;

/// <summary>
/// The command rows this process knows about: added when the queue inserts one, removed when the
/// executor is done with it. A Queued or Started row that is not in here has been stranded by a failed
/// status write, so the queue's dedupe must not let it swallow a new enqueue. A singleton shared by the
/// queue and the executor.
/// </summary>
public sealed class ActiveCommands
{
    private readonly ConcurrentDictionary<long, byte> _ids = new();

    /// <summary>
    /// Gets the gate the queue holds while it writes a row and records it here (and while it cancels one).
    /// The executor's start-up sweep takes it too, so it never sees a row that is saved but not yet held.
    /// </summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancellable = new();

    /// <summary>Records the token source of a running command whose handler lets a user cancel it.</summary>
    /// <param name="id">The command row.</param>
    /// <param name="source">What the handler's token comes from.</param>
    internal void RegisterCancellable(long id, CancellationTokenSource source) => _cancellable[id] = source;

    /// <summary>Forgets the token source; called before the executor disposes it.</summary>
    /// <param name="id">The command row.</param>
    internal void UnregisterCancellable(long id) => _cancellable.TryRemove(id, out _);

    /// <summary>
    /// Fires the cancellation of a running command, when its handler opted in to being cancelled.
    /// </summary>
    /// <param name="id">The command row.</param>
    /// <returns><see langword="true"/> when the command was running and is now being cancelled.</returns>
    internal bool TryCancelRunning(long id)
    {
        if (!_cancellable.TryGetValue(id, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();

            return true;
        }
        catch (ObjectDisposedException)
        {
            // The command finished between the lookup and the cancel.
            return false;
        }
    }

    /// <summary>Records that the process holds the command.</summary>
    /// <param name="id">The command row.</param>
    public void Add(long id) => _ids[id] = 0;

    /// <summary>Records that the process is done with the command.</summary>
    /// <param name="id">The command row.</param>
    public void Remove(long id) => _ids.TryRemove(id, out _);

    /// <summary>The ids held right now, for a caller that has to judge rows read afterwards against what was held before the read.</summary>
    /// <returns>A copy of the held ids.</returns>
    public HashSet<long> Snapshot() => [.. _ids.Keys];

    /// <summary>Whether the process still holds the command.</summary>
    /// <param name="id">The command row.</param>
    /// <returns><see langword="true"/> when the executor may yet run or finish it.</returns>
    public bool Contains(long id) => _ids.ContainsKey(id);
}
