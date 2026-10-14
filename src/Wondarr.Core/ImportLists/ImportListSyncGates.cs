using System.Collections.Concurrent;

namespace Wondarr.Core.ImportLists;

/// <summary>
/// One gate per import list, so two syncs of the same list never overlap (R-020). "Sync now" and the
/// hourly run are different commands: a long first sync sets <c>LastSyncedAt</c> only at its end, so the
/// hourly run would otherwise find the list due and read it again beside the first, creating a second
/// Plex playlist and a second row for each item. One instance per host (a singleton in DI), so the gates
/// are never shared between hosts that happen to share a process, whose lists can have the same ids.
/// </summary>
public sealed class ImportListSyncGates
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    /// <summary>The gate of one list; the same instance for every caller in the host.</summary>
    /// <param name="importListId">The import list.</param>
    /// <returns>The gate, with a count of one.</returns>
    public SemaphoreSlim For(long importListId) =>
        _gates.GetOrAdd(importListId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Takes the list's gate, waiting if a sync of the list is running. While it waits, the command's
    /// executor place is handed back (as a search waiting for a download slot does), so a manual sync
    /// queued behind a long one does not hold one of the few places idle.
    /// </summary>
    /// <param name="importListId">The import list.</param>
    /// <param name="yieldWorker">The command's <c>YieldWorker</c>, or <see langword="null"/> outside the executor.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A handle that releases the gate when disposed.</returns>
    public async Task<IDisposable> EnterAsync(
        long importListId,
        Func<IAsyncDisposable?>? yieldWorker,
        CancellationToken cancellationToken)
    {
        var gate = For(importListId);

        if (!await gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            var lease = yieldWorker?.Invoke();

            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return new Release(gate);
    }

    /// <summary>Takes the list's gate only if it is free right now.</summary>
    /// <param name="importListId">The import list.</param>
    /// <returns>A handle that releases the gate when disposed, or <see langword="null"/> when a sync of the list is running.</returns>
    public IDisposable? TryEnter(long importListId)
    {
        var gate = For(importListId);

        return gate.Wait(0) ? new Release(gate) : null;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
