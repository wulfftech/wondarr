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
