using Wondarr.Core.Domain;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Blocklisting;

/// <summary>Reads, writes and expires the blocklist the search pipeline checks before every grab.</summary>
public interface IBlocklistService
{
    /// <summary>Adds one entry.</summary>
    /// <param name="item">The entry to store; its <c>CreatedAt</c> is stamped by the context.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored entry, with its id and timestamps set.</returns>
    Task<BlocklistItem> AddAsync(BlocklistItem item, CancellationToken cancellationToken);

    /// <summary>Gets a page of entries, newest first by default.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<BlocklistItem>> GetPageAsync(PagingSpec paging, CancellationToken cancellationToken);

    /// <summary>Gets a page of entries recorded for one song, newest first by default.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="songId">Only entries recorded for this song, or <see langword="null"/> for every entry.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<BlocklistItem>> GetPageAsync(PagingSpec paging, long? songId, CancellationToken cancellationToken);

    /// <summary>Removes one entry, so the candidate may be grabbed again.</summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a row was removed, <see langword="false"/> when there was none.</returns>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Gets a value indicating whether a source key is currently blocked.</summary>
    /// <param name="sourceType">The source the key belongs to, for example <c>slskd</c>.</param>
    /// <param name="key">The source-specific key.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<bool> IsBlocklistedAsync(string sourceType, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the keys a search must treat as blocked before judging its candidates, in one query.
    /// A song's searches see the entries recorded for it <em>and</em> the song-less ones: a file that
    /// was wrong for one song may be the right file for another (a live take, a remix).
    /// </summary>
    /// <param name="sourceType">The source the keys belong to, for example <c>soulseek</c>.</param>
    /// <param name="songId">The song being searched for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlySet<string>> GetActiveKeysAsync(string sourceType, long songId, CancellationToken cancellationToken);
}

/// <summary>
/// Backs <c>/api/v1/blocklist</c>. A row blocks a candidate until <see cref="BlocklistItem.ExpiresAt"/>
/// passes; a row without one blocks forever. Expiry is compared against <see cref="TimeProvider"/>, so
/// tests can move "now" without waiting.
/// </summary>
public sealed class BlocklistService : IBlocklistService
{
    private readonly WondarrDbContext _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="BlocklistService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="timeProvider">The clock used to decide whether an entry has expired.</param>
    public BlocklistService(WondarrDbContext database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<BlocklistItem> AddAsync(BlocklistItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        _database.Blocklist.Add(item);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return item;
    }

    /// <inheritdoc />
    public Task<PagedResult<BlocklistItem>> GetPageAsync(PagingSpec paging, CancellationToken cancellationToken) =>
        GetPageAsync(paging, songId: null, cancellationToken);

    /// <inheritdoc />
    public async Task<PagedResult<BlocklistItem>> GetPageAsync(
        PagingSpec paging,
        long? songId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.Blocklist.AsNoTracking().Include(item => item.Song).AsQueryable();

        if (songId is { } id)
        {
            query = query.Where(item => item.SongId == id);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<BlocklistItem>(records, totalRecords);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var item = await _database.Blocklist
            .FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return false;
        }

        _database.Blocklist.Remove(item);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> IsBlocklistedAsync(string sourceType, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceType);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return await _database.Blocklist
            .AsNoTracking()
            .AnyAsync(
                item => item.SourceType == sourceType
                    && item.BlocklistKey == key
                    && (item.ExpiresAt == null || item.ExpiresAt > now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetActiveKeysAsync(
        string sourceType,
        long songId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceType);

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var keys = await _database.Blocklist
            .AsNoTracking()
            .Where(item => item.SourceType == sourceType
                && (item.SongId == null || item.SongId == songId)
                && (item.ExpiresAt == null || item.ExpiresAt > now))
            .Select(item => item.BlocklistKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The only sort key is <c>date</c> (the row's <c>CreatedAt</c>); anything unknown falls back to
    /// it. The id breaks ties so paging cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<BlocklistItem> ApplySort(IQueryable<BlocklistItem> query, PagingSpec paging) =>
        paging.Descending
            ? query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            : query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id);
}
