using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Searching;

/// <summary>The wait both batch loops (missing and upgrade) make for a free download slot.</summary>
internal static class SlotWaiting
{
    /// <summary>How long the batch waits between two warnings that every download slot is taken.</summary>
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Waits until fewer than <see cref="SearchOptions.MaxActiveDownloads"/> grabs are in flight. The
    /// wait hands the command's executor place back (a batch can wait for hours, and three waiting
    /// commands would otherwise keep every other command from starting). The place is taken back
    /// before the wait ends, and the slot is counted again with it in hand, because a slot that was
    /// free while the place was being waited for may be gone by then.
    /// </summary>
    /// <param name="scopes">Builds the scope the slots are counted in.</param>
    /// <param name="timeProvider">The clock the wait is measured against.</param>
    /// <param name="context">The command, for its executor place.</param>
    /// <param name="options">The slot limit and the poll interval.</param>
    /// <param name="warn">Told how many downloads are active, at most once every ten minutes.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal static async Task WaitForFreeSlotAsync(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        CommandContext context,
        SearchOptions options,
        Action<int> warn,
        CancellationToken cancellationToken)
    {
        var lastWarning = timeProvider.GetUtcNow();
        IAsyncDisposable? lease = null;

        try
        {
            while (true)
            {
                int active;

                using (var scope = scopes.CreateScope())
                {
                    var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

                    active = await database.QueueItems
                        .AsNoTracking()
                        .CountAsync(
                            item => item.State == QueueItemState.Queued
                                || item.State == QueueItemState.RemotelyQueued
                                || item.State == QueueItemState.Downloading,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (active < options.MaxActiveDownloads)
                {
                    if (lease is null)
                    {
                        return;
                    }

                    // Work again only with a place of our own, then look at the slots once more.
                    var held = lease;
                    lease = null;
                    await held.DisposeAsync().ConfigureAwait(false);

                    continue;
                }

                // A stall here is not visible anywhere else (the batch simply stops), so the wait says so
                // out loud, but only once every ten minutes rather than on every poll.
                var now = timeProvider.GetUtcNow();

                if (now - lastWarning >= WarningInterval)
                {
                    warn(active);
                    lastWarning = now;
                }

                lease ??= context.YieldWorker?.Invoke();

                await Task.Delay(TimeSpan.FromSeconds(options.SlotWaitSeconds), timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
