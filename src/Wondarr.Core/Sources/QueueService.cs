using Wondarr.Core.Domain;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Sources;

/// <summary>Reads and writes the download queue.</summary>
public interface IQueueService
{
    /// <summary>Records a new grab.</summary>
    /// <param name="item">The grab; its state and progress timestamps are stamped now.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored item, with its id set.</returns>
    Task<QueueItem> AddAsync(QueueItem item, CancellationToken cancellationToken);

    /// <summary>Gets one queue item with its song, or <see langword="null"/>.</summary>
    /// <param name="id">The queue item id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<QueueItem?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Gets everything that is not finished, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<QueueItem>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Whether the song already has a grab in flight.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<bool> HasActiveForSongAsync(long songId, CancellationToken cancellationToken);

    /// <summary>Writes back an item the caller advanced, stamping the timestamps that moved.</summary>
    /// <param name="item">The item; it must be the instance this context loaded.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task UpdateAsync(QueueItem item, CancellationToken cancellationToken);

    /// <summary>Gets a page of queue items, newest first by default.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<QueueItem>> GetPageAsync(PagingSpec paging, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of queue items with the candidate each one carries loaded, newest first by
    /// default, optionally limited to the items that are still in flight (<c>/api/v1/queue</c>).
    /// </summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="includeFinished">Whether imported, failed and cancelled items are on the page too.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<QueueItem>> GetPageAsync(PagingSpec paging, bool includeFinished, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of queue items, as the overload without a song does, optionally limited to the
    /// grabs made for one song.
    /// </summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="includeFinished">Whether imported, failed and cancelled items are on the page too.</param>
    /// <param name="songId">Only items grabbed for this song, or <see langword="null"/> for every item.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<QueueItem>> GetPageAsync(
        PagingSpec paging,
        bool includeFinished,
        long? songId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Backs <c>/api/v1/queue</c> and the queue poll. A grab is "active" until it is imported, failed or
/// cancelled; that is what stops the search loop grabbing the same song twice.
/// </summary>
public sealed class QueueService : IQueueService
{
    private readonly WondarrDbContext _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="QueueService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="timeProvider">The clock used to stamp state and progress changes.</param>
    public QueueService(WondarrDbContext database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<QueueItem> AddAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        item.StateChangedAt = now;
        item.LastProgressAt = now;

        _database.QueueItems.Add(item);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return item;
    }

    /// <inheritdoc />
    public async Task<QueueItem?> GetAsync(long id, CancellationToken cancellationToken) =>
        await _database.QueueItems
            .Include(item => item.Song)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueItem>> GetActiveAsync(CancellationToken cancellationToken) =>
        await _database.QueueItems
            .AsNoTracking()
            .Where(item =>
                item.State == QueueItemState.Queued
                || item.State == QueueItemState.RemotelyQueued
                || item.State == QueueItemState.Downloading
                || item.State == QueueItemState.Completed
                || item.State == QueueItemState.Importing)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> HasActiveForSongAsync(long songId, CancellationToken cancellationToken) =>
        await _database.QueueItems
            .AsNoTracking()
            .AnyAsync(
                item => item.SongId == songId
                    && (item.State == QueueItemState.Queued
                        || item.State == QueueItemState.RemotelyQueued
                        || item.State == QueueItemState.Downloading
                        || item.State == QueueItemState.Completed
                        || item.State == QueueItemState.Importing),
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task UpdateAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var entry = _database.Entry(item);

        if (entry.State == EntityState.Detached)
        {
            // The caller handed back a plain object (the API does), so there is nothing to compare
            // against: treat it as a state change rather than silently keeping a stale timestamp.
            _database.QueueItems.Update(item);
            item.StateChangedAt = now;
            item.LastProgressAt = now;
        }
        else
        {
            var previousState = entry.OriginalValues.GetValue<QueueItemState>(nameof(QueueItem.State));
            var previousBytes = entry.OriginalValues.GetValue<long>(nameof(QueueItem.BytesTransferred));

            if (previousState != item.State)
            {
                item.StateChangedAt = now;
            }

            if (item.BytesTransferred > previousBytes)
            {
                item.LastProgressAt = now;
            }
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PagedResult<QueueItem>> GetPageAsync(PagingSpec paging, CancellationToken cancellationToken) =>
        GetPageAsync(paging, includeFinished: true, cancellationToken);

    /// <inheritdoc />
    public Task<PagedResult<QueueItem>> GetPageAsync(
        PagingSpec paging,
        bool includeFinished,
        CancellationToken cancellationToken) =>
        GetPageAsync(paging, includeFinished, songId: null, cancellationToken);

    /// <inheritdoc />
    public async Task<PagedResult<QueueItem>> GetPageAsync(
        PagingSpec paging,
        bool includeFinished,
        long? songId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.QueueItems
            .AsNoTracking()
            .Include(item => item.Song)
            .Include(item => item.Candidate)
            .AsQueryable();

        if (songId is { } id)
        {
            query = query.Where(item => item.SongId == id);
        }

        if (!includeFinished)
        {
            // The same five states the poll and HasActiveForSongAsync call active: an item is in
            // flight until it is imported, failed or cancelled.
            query = query.Where(item =>
                item.State == QueueItemState.Queued
                || item.State == QueueItemState.RemotelyQueued
                || item.State == QueueItemState.Downloading
                || item.State == QueueItemState.Completed
                || item.State == QueueItemState.Importing);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<QueueItem>(records, totalRecords);
    }

    /// <summary>
    /// Sort keys <c>createdAt</c> (the default), <c>state</c> and <c>progress</c>; anything unknown falls
    /// back to the default. The id breaks ties so paging cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<QueueItem> ApplySort(IQueryable<QueueItem> query, PagingSpec paging) =>
        paging.SortKey?.ToLowerInvariant() switch
        {
            "state" when paging.Descending => query.OrderByDescending(item => item.State).ThenByDescending(item => item.Id),
            "state" => query.OrderBy(item => item.State).ThenBy(item => item.Id),
            "progress" when paging.Descending => query.OrderByDescending(item => item.Progress).ThenByDescending(item => item.Id),
            "progress" => query.OrderBy(item => item.Progress).ThenBy(item => item.Id),
            _ when paging.Descending => query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id),
            _ => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
        };
}
