using Compilarr.Core.Domain;
using Compilarr.Core.Paging;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compilarr.Core.History;

/// <summary>Reads and writes the song lifecycle log.</summary>
public interface IHistoryService
{
    /// <summary>Appends one event.</summary>
    /// <param name="item">The event to store; its <c>CreatedAt</c> is stamped by the context.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored event, with its id and timestamps set.</returns>
    Task<HistoryItem> AddAsync(HistoryItem item, CancellationToken cancellationToken);

    /// <summary>Gets a page of events, newest first by default.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="songId">Only events for this song, or <see langword="null"/> for every song.</param>
    /// <param name="eventType">Only events of this type, or <see langword="null"/> for every type.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<HistoryItem>> GetPageAsync(
        PagingSpec paging,
        long? songId,
        HistoryEventType? eventType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Backs <c>/api/v1/history</c>. Later phases write the rows from real grabs and imports; Phase 1
/// only needs the table, the service and the endpoint. The event time is the row's
/// <see cref="EntityBase.CreatedAt"/>.
/// </summary>
public sealed class HistoryService : IHistoryService
{
    private readonly CompilarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="HistoryService"/> class.</summary>
    /// <param name="database">The Compilarr database.</param>
    public HistoryService(CompilarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public async Task<HistoryItem> AddAsync(HistoryItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        _database.History.Add(item);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return item;
    }

    /// <inheritdoc />
    public async Task<PagedResult<HistoryItem>> GetPageAsync(
        PagingSpec paging,
        long? songId,
        HistoryEventType? eventType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.History
            .AsNoTracking()
            .Include(item => item.Song)
                .ThenInclude(song => song.PrimaryArtist)
            .Include(item => item.Song)
                .ThenInclude(song => song.AlbumContext)
            .Include(item => item.Song)
                .ThenInclude(song => song.File)
            .AsQueryable();

        if (songId is { } id)
        {
            query = query.Where(item => item.SongId == id);
        }

        if (eventType is { } type)
        {
            query = query.Where(item => item.EventType == type);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<HistoryItem>(records, totalRecords);
    }

    /// <summary>
    /// The only sort key is <c>date</c> (the row's <c>CreatedAt</c>); anything unknown falls back to
    /// it. The id breaks ties so paging cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<HistoryItem> ApplySort(IQueryable<HistoryItem> query, PagingSpec paging) =>
        paging.Descending
            ? query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            : query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id);
}
