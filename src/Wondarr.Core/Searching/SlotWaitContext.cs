namespace Wondarr.Core.Searching;

/// <summary>
/// What a search that has to wait for a download slot may use from the command that runs it: a way to
/// say so on the Tasks page, and a way to hand the executor worker back while it waits. One instance
/// per DI scope, filled in by the command handler; empty for a search that is not a command (the
/// missing-song batch, an API call), which then waits on its own thread.
/// </summary>
public sealed class SlotWaitContext
{
    /// <summary>
    /// Gets or sets whether the search may wait for a download slot. Only the <c>SongSearch</c> command
    /// sets it; the batch loops wait for a slot before each song themselves and must not hold their
    /// executor place inside a song.
    /// </summary>
    public bool WaitForSlot { get; set; }

    /// <summary>Gets or sets the command's progress report, or <see langword="null"/> outside a command.</summary>
    public Func<string, Task>? ReportProgressAsync { get; set; }

    /// <summary>
    /// Gets or sets the command's worker hand-back (see
    /// <see cref="Jobs.CommandContext.YieldWorker"/>), or <see langword="null"/> outside a command.
    /// </summary>
    public Func<IAsyncDisposable?>? YieldWorker { get; set; }
}

/// <summary>The songs whose searches are parked waiting for a download slot, so no second run starts for them.</summary>
public sealed class SlotWaiters
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _songs = new();

    /// <summary>Whether a search for the song is waiting for a slot.</summary>
    /// <param name="songId">The song.</param>
    public bool IsWaiting(long songId) => _songs.ContainsKey(songId);

    internal void Add(long songId) => _songs[songId] = 1;

    internal void Remove(long songId) => _songs.TryRemove(songId, out _);
}
