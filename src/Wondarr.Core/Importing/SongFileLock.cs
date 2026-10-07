using System.Collections.Concurrent;

namespace Wondarr.Core.Importing;

/// <summary>
/// One process-wide lock per song, so that an import's place-and-record step and a compaction's
/// stage step can never act on the same song's file at the same time (LIBRARY_OUTPUT §7.3,
/// MATCHING_ENGINE §6.6). The import takes only this lock; the compaction takes its library gate
/// first and then this one — never the other way round.
/// </summary>
public interface ISongFileLock
{
    /// <summary>
    /// Waits for the song's file lock and returns a handle that releases it when disposed.
    /// </summary>
    /// <param name="songId">The song whose file is being worked on.</param>
    /// <param name="cancellationToken">Cancels the wait; the song's reference is released when it fires.</param>
    /// <returns>A handle that releases the lock when disposed.</returns>
    ValueTask<IAsyncDisposable> AcquireAsync(long songId, CancellationToken cancellationToken);
}

/// <summary>
/// The one implementation of <see cref="ISongFileLock"/>: a keyed <see cref="SemaphoreSlim"/>(1,1)
/// per song id, with reference counting so an entry disappears as soon as nobody holds or waits for
/// it — the dictionary cannot grow without bound as songs come and go.
/// </summary>
public sealed class SongFileLock : ISongFileLock
{
    /// <summary>One song's semaphore and the number of holders and waiters it has.</summary>
    private sealed class Entry
    {
        /// <summary>The song's own one-at-a-time gate.</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>How many holders and waiters the entry still has; it is dropped when this reaches zero.</summary>
        public int References;
    }

    /// <summary>Serialises the reference counting and the dictionary's own changes.</summary>
    private readonly object _gate = new();

    /// <summary>One entry per song that somebody holds or waits for.</summary>
    private readonly ConcurrentDictionary<long, Entry> _entries = new();

    /// <summary>How many song entries exist right now; for the tests, which prove the dictionary empties.</summary>
    internal int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(long songId, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(songId, _ => new Entry());

        lock (_gate)
        {
            // Counted before the wait: while this waiter exists, the entry cannot be dropped.
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The wait gave up (cancellation above all): its reference goes with it.
            ReleaseReference(songId, entry);

            throw;
        }

        return new Handle(this, songId, entry);
    }

    /// <summary>Gives up one reference, dropping the entry when nobody holds or waits for it any more.</summary>
    private void ReleaseReference(long songId, Entry entry)
    {
        lock (_gate)
        {
            entry.References--;

            if (entry.References == 0
                && _entries.TryGetValue(songId, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.TryRemove(songId, out _);
            }
        }
    }

    /// <summary>Releases the song's semaphore and the holder's reference, exactly once.</summary>
    private sealed class Handle(SongFileLock owner, long songId, Entry entry) : IAsyncDisposable
    {
        private int _released;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            entry.Semaphore.Release();
            owner.ReleaseReference(songId, entry);

            return ValueTask.CompletedTask;
        }
    }
}