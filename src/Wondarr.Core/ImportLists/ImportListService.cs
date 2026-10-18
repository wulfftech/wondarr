using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;

namespace Wondarr.Core.ImportLists;

/// <summary>What the user sends to create or change a synced import list.</summary>
/// <param name="Type">The provider type, for example <c>csv</c>.</param>
/// <param name="Name">The list's display name; also the name of its playlist.</param>
/// <param name="Settings">The provider's settings, as a JSON object.</param>
/// <param name="SourceText">The uploaded content (a CSV file's text), or <see langword="null"/> to keep what is stored.</param>
/// <param name="Policy">What a sync does with items that left the source: <c>AddOnly</c>, <c>AddAndUnmonitor</c> or <c>Mirror</c>.</param>
/// <param name="QualityProfileId">The profile new songs are monitored against, or <see langword="null"/> for the default.</param>
/// <param name="LibraryId">The library new songs are filed in, or <see langword="null"/> for the default.</param>
/// <param name="Enabled">Whether the scheduled sync reads the list.</param>
/// <param name="SyncIntervalHours">Hours between scheduled syncs; 0 = only when asked.</param>
/// <param name="PlexPlaylist">Whether the list is kept as a Plex playlist.</param>
/// <param name="M3uExport">Whether the list is written as an <c>.m3u8</c>.</param>
public sealed record ImportListDraft(
    string Type,
    string Name,
    JsonElement Settings,
    string? SourceText,
    string? Policy,
    long? QualityProfileId,
    long? LibraryId,
    bool Enabled = true,
    int SyncIntervalHours = 24,
    bool? PlexPlaylist = null,
    bool? M3uExport = null);

/// <summary>A list with the counts of its items, as the list screens show it.</summary>
/// <param name="List">The list.</param>
/// <param name="Pending">Items not looked up yet.</param>
/// <param name="Added">Items added (or already in the library).</param>
/// <param name="Unresolved">Items waiting for the review screen.</param>
/// <param name="Skipped">Items deliberately not added.</param>
/// <param name="Removed">Items a sync found gone from the source.</param>
public sealed record ImportListView(
    ImportList List,
    int Pending,
    int Added,
    int Unresolved,
    int Skipped,
    int Removed);

/// <summary>A draft the list cannot be saved with, naming the field at fault.</summary>
public sealed class ImportListValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ImportListValidationException"/> class.</summary>
    /// <param name="field">The field at fault, as the API names it.</param>
    /// <param name="detail">What is wrong with it.</param>
    public ImportListValidationException(string field, string detail)
        : base($"{field}: {detail}")
    {
        Field = field;
        Detail = detail;
    }

    /// <summary>Gets the field at fault, as the API names it.</summary>
    public string Field { get; }

    /// <summary>Gets what is wrong with it.</summary>
    public string Detail { get; }
}

/// <summary>Synced import lists: their settings, and the sync that reads, resolves and adds.</summary>
public interface IImportListService
{
    /// <summary>Gets every registered provider.</summary>
    IReadOnlyList<IImportListProvider> Providers { get; }

    /// <summary>Lists the import lists, newest first.</summary>
    /// <param name="includePasted">Whether pasted lists (Phase 1's bulk add) are included.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<ImportListView>> ListAsync(bool includePasted, CancellationToken cancellationToken);

    /// <summary>Reads one list with its counts.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<ImportListView?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Creates a synced list. It is not read until it is synced.</summary>
    /// <param name="draft">The list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ImportListValidationException">The draft cannot be saved.</exception>
    Task<ImportList> CreateAsync(ImportListDraft draft, CancellationToken cancellationToken);

    /// <summary>Changes a synced list. Its items stay; the next sync reads the source again.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="draft">The new settings; a secret sent back masked keeps its stored value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ImportListValidationException">The draft cannot be saved.</exception>
    Task<ImportList?> UpdateAsync(long id, ImportListDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes a list and its items. The songs it added stay in the library.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the list's source, records which items are new, still there or gone, then resolves and
    /// adds the new ones in one batch.
    /// </summary>
    /// <param name="id">The list id.</param>
    /// <param name="reportProgress">Receives a progress line now and then.</param>
    /// <param name="cancellationToken">Cancels the sync.</param>
    /// <returns>The one-line result.</returns>
    /// <exception cref="ImportListSyncException">The source could not be read.</exception>
    Task<string> SyncAsync(long id, Func<string, Task> reportProgress, CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites the playlists of every enabled list that writes one, so songs that got their files
    /// since the last sync appear in them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>How many lists' playlists were written.</returns>
    Task<int> RefreshPlaylistsAsync(CancellationToken cancellationToken);

    /// <summary>The ids of the enabled synced lists whose interval has passed, oldest sync first.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<long>> GetDueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Queues a <c>SongSearch</c> for each song a synced list added and has not searched for yet, once
    /// no reference library is still identifying its files, or once the song has waited 24 hours
    /// (owner question 15). Does nothing while <c>search.search_on_add</c> is off. A song is queued
    /// once: its items remember it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many searches were queued.</returns>
    Task<int> QueueSearchForWaitingSongsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Makes the next read of the list trusted whatever its size: it removes what is missing from it,
    /// even when it is empty or far shorter than the last. The flag clears after that read.
    /// </summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The list, or <see langword="null"/> when the id is unknown or the list is a pasted one.</returns>
    Task<ImportList?> TrustNextReadAsync(long id, CancellationToken cancellationToken);
}

/// <summary>A sync whose source could not be read; the list records the reason.</summary>
public sealed class ImportListSyncException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ImportListSyncException"/> class.</summary>
    /// <param name="message">Why the source could not be read.</param>
    public ImportListSyncException(string message)
        : base(message)
    {
    }
}

/// <summary>The import-list service.</summary>
public sealed partial class ImportListService : IImportListService
{
    /// <summary>The most items one synced list holds; a longer source is cut, and the sync says so.</summary>
    public const int MaxItems = 10_000;

    /// <summary>
    /// The size from which a read far below the last complete one is suspect (R-007): a list of fewer
    /// items than this can legitimately lose half of them, and only an empty read of one is doubted.
    /// </summary>
    public const int SuspectMinimumItems = 20;

    /// <summary>How long Mirror waits for a song's file lock before it leaves the song alone.</summary>
    private static readonly TimeSpan FileLockWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest uploaded file, in characters (about 5 MB of CSV).</summary>
    public const int MaxSourceTextLength = 5 * 1024 * 1024;

    /// <summary>The policy that also unmonitors songs only this list wanted, when they leave it.</summary>
    public const string AddAndUnmonitorPolicy = "AddAndUnmonitor";

    /// <summary>The policy that also deletes such songs when they have no file (and unmonitors them when they do).</summary>
    public const string MirrorPolicy = "Mirror";

    /// <summary>The policies a list may name.</summary>
    public static readonly IReadOnlyList<string> Policies = [ImportList.AddOnlyPolicy, AddAndUnmonitorPolicy, MirrorPolicy];

    private readonly WondarrDbContext _database;
    private readonly IPasteListService _items;
    private readonly ISongService _songs;
    private readonly ISongFileLock _fileLock;
    private readonly ICommandQueue _commands;
    private readonly IOptionsMonitor<SearchOptions> _searchOptions;
    private readonly IPlaylistWriter _playlists;
    private readonly ImportListSyncGates _gates;
    private readonly IReadOnlyList<IImportListProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportListService> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportListService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="items">The item pipeline the bulk add uses: resolve, then one batch add.</param>
    /// <param name="songs">The song service, for the policies' unmonitor and delete.</param>
    /// <param name="fileLock">The per-song file lock, held while Mirror deletes a song so no import places its file meanwhile.</param>
    /// <param name="commands">The command queue, for the search that follows the first reference scan.</param>
    /// <param name="searchOptions">The search options; <c>search_on_add</c> off means no search is queued from here.</param>
    /// <param name="playlists">Writes the list's playlists after a sync.</param>
    /// <param name="gates">The per-list sync gates of this host.</param>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ImportListService(
        WondarrDbContext database,
        IPasteListService items,
        ISongService songs,
        ISongFileLock fileLock,
        ICommandQueue commands,
        IOptionsMonitor<SearchOptions> searchOptions,
        IPlaylistWriter playlists,
        ImportListSyncGates gates,
        IEnumerable<IImportListProvider> providers,
        TimeProvider timeProvider,
        ILogger<ImportListService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(fileLock);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(searchOptions);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _items = items;
        _songs = songs;
        _fileLock = fileLock;
        _commands = commands;
        _searchOptions = searchOptions;
        _playlists = playlists;
        _gates = gates;
        _providers = [.. providers];
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<IImportListProvider> Providers => _providers;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportListView>> ListAsync(bool includePasted, CancellationToken cancellationToken)
    {
        var query = _database.ImportLists.AsNoTracking();

        if (!includePasted)
        {
            query = query.Where(list => list.Type != ImportList.PasteType);
        }

        var lists = await query
            .OrderByDescending(list => list.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = lists.Select(list => list.Id).ToList();
        var counts = await CountsAsync(ids, cancellationToken).ConfigureAwait(false);

        return [.. lists.Select(list => View(list, counts))];
    }

    /// <inheritdoc />
    public async Task<ImportListView?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var list = await _database.ImportLists
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return null;
        }

        var counts = await CountsAsync([id], cancellationToken).ConfigureAwait(false);

        return View(list, counts);
    }

    /// <inheritdoc />
    public async Task<ImportList> CreateAsync(ImportListDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var provider = Provider(draft.Type);
        var list = new ImportList { Type = provider.Type };

        await ApplyAsync(list, draft, provider, storedSettings: null, cancellationToken).ConfigureAwait(false);

        _database.ImportLists.Add(list);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(_logger, list.Id, list.Type);

        return list;
    }

    /// <inheritdoc />
    public async Task<ImportList?> UpdateAsync(long id, ImportListDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return null;
        }

        if (list.Type == ImportList.PasteType)
        {
            throw new ImportListValidationException("type", "A pasted list has no settings to change.");
        }

        if (!string.Equals(draft.Type, list.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportListValidationException("type", "A list's type cannot be changed; create a new list instead.");
        }

        var (settingsBefore, sourceBefore) = (list.Settings, list.SourceText);

        await ApplyAsync(list, draft, Provider(list.Type), list.Settings, cancellationToken).ConfigureAwait(false);

        // A different source (a new playlist, a new file) has no "usual size", so its next read is not
        // held to the "under half" rule. Only the provider's non-secret fields count: a rotated API
        // key or reordered keys do not make a different source.
        if (SourceChanged(Provider(list.Type), settingsBefore, sourceBefore, list))
        {
            list.SourceEditedSinceRead = true;
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return list;
    }

    /// <inheritdoc />
    public async Task<ImportList?> TrustNextReadAsync(long id, CancellationToken cancellationToken)
    {
        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.Type != ImportList.PasteType, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return null;
        }

        list.TrustNextRead = true;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogTrustNextRead(_logger, id);

        return list;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return false;
        }

        // The items cascade; the songs they added are the user's now and stay.
        _database.ImportLists.Remove(list);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogDeleted(_logger, id);

        return true;
    }

    /// <inheritdoc />
    public async Task<string> SyncAsync(long id, Func<string, Task> reportProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"Import list {id.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(id));

        if (list.Type == ImportList.PasteType)
        {
            return "A pasted list is resolved once and has no source to sync.";
        }

        var provider = FindProvider(list.Type);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (provider is null)
        {
            return await FailAsync(list, $"No provider reads lists of type '{list.Type}'.", now, cancellationToken)
                .ConfigureAwait(false);
        }

        await reportProgress($"Reading {list.Name}").ConfigureAwait(false);

        ImportListFetchResult fetched;

        try
        {
            fetched = await provider.FetchAsync(list, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or MetadataProviderException
            or TaskCanceledException
            or JsonException
            or FormatException)
        {
            // The message is stored on the list and shown by the API, so it is written here rather
            // than copied from the exception, whose text may quote a request URL (and a key in it).
            fetched = ImportListFetchResult.Failed(Describe(exception));
        }

        if (!fetched.Success)
        {
            return await FailAsync(list, fetched.Error!, now, cancellationToken).ConfigureAwait(false);
        }

        var diff = await ApplyFetchAsync(list, fetched, now, cancellationToken).ConfigureAwait(false);

        // The same pipeline a pasted list runs: pending items are resolved one by one, then added in
        // one batch so the album policy sees an artist's songs together and one search is queued.
        var processed = await _items.ProcessAsync(list.Id, reportProgress, cancellationToken).ConfigureAwait(false);

        // A suspect read has no gone items, so the policy finds nothing to act on.
        var policy = await ApplyPolicyAsync(list, diff.GoneItemIds, cancellationToken).ConfigureAwait(false);
        var playlists = await _playlists.WriteAsync(list.Id, cancellationToken).ConfigureAwait(false);

        var returned = diff.Returned == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $", {diff.Returned} back");
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Read {diff.Read} items ({diff.New} new{returned}, {diff.Gone} no longer in the list{(diff.Cut ? $", cut at {MaxItems}" : string.Empty)}); {processed}{policy}{(playlists is null ? string.Empty : "; " + playlists)}");

        list.LastSyncedAt = now;
        var full = diff.Suspect is null ? message : diff.Suspect + ". " + message;
        list.LastSyncMessage = full;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (diff.Suspect is not null)
        {
            LogSuspectRead(_logger, list.Id, diff.Suspect);
        }

        LogSynced(_logger, list.Id, diff.Read, diff.New, diff.Gone);

        return full;
    }

    /// <inheritdoc />
    public async Task<int> QueueSearchForWaitingSongsAsync(CancellationToken cancellationToken)
    {
        // The user's own switch: with search-on-add off, searching is the scheduled MissingSearch's job.
        if (!_searchOptions.CurrentValue.SearchOnAdd)
        {
            return 0;
        }

        var scanning = (await ReferenceScanWait.PendingLibrariesAsync(_database, cancellationToken).ConfigureAwait(false)).Count > 0;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // While a scan is outstanding only the songs that have waited the full cap are searched.
        var cutoff = scanning ? now - ReferenceScanWait.MaxWait : DateTime.MaxValue;

        var syncedListIds = await _database.ImportLists
            .AsNoTracking()
            .Where(list => list.Type != ImportList.PasteType)
            .Select(list => list.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (syncedListIds.Count == 0)
        {
            return 0;
        }

        var tags = syncedListIds.Select(id => string.Create(CultureInfo.InvariantCulture, $"list:{id}")).ToList();

        // A song a synced list added that has never been searched for (no search run) and whose search
        // this method has not queued yet (an item of it with no SearchQueuedAt).
        var batch = Math.Max(1, _searchOptions.CurrentValue.MissingBatchSize);
        var waiting = await _database.Songs
            .AsNoTracking()
            .Where(song => tags.Contains(song.AddedBy)
                && song.Monitored
                && song.File == null
                && song.CreatedAt < cutoff
                && !_database.SearchRuns.Any(run => run.SongId == song.Id)
                && _database.ImportListItems.Any(item => item.SongId == song.Id && item.SearchQueuedAt == null))
            .OrderBy(song => song.Id)
            .Select(song => song.Id)
            .Take(batch)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var songId in waiting)
        {
            await _commands
                .EnqueueAsync(
                    SongSearchCommandHandler.CommandName,
                    JsonSerializer.Serialize(new { name = SongSearchCommandHandler.CommandName, songId }, SearchBodyJson),
                    CommandTrigger.Unspecified,
                    cancellationToken)
                .ConfigureAwait(false);

            var items = await _database.ImportListItems
                .Where(item => item.SongId == songId && item.SearchQueuedAt == null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in items)
            {
                item.SearchQueuedAt = now;
            }

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return waiting.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetDueAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await _database.ImportLists
            .AsNoTracking()
            .Where(list => list.Enabled && list.Type != ImportList.PasteType && list.SyncIntervalHours > 0)
            .Select(list => new { list.Id, list.LastSyncedAt, list.SyncIntervalHours })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. candidates
            .Where(list => list.LastSyncedAt is null
                || list.LastSyncedAt.Value.AddHours(list.SyncIntervalHours) <= now)
            .OrderBy(list => list.LastSyncedAt ?? DateTime.MinValue)
            .ThenBy(list => list.Id)
            .Select(list => list.Id)];
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => string.Create(
            CultureInfo.InvariantCulture,
            $"The source answered HTTP {(int)status}."),
        HttpRequestException => "The source could not be reached.",
        TaskCanceledException => "The source did not answer in time.",
        JsonException or FormatException => "The source sent something that could not be read.",
        _ => "The source could not be read.",
    };

    /// <inheritdoc />
    public async Task<int> RefreshPlaylistsAsync(CancellationToken cancellationToken)
    {
        var ids = await _database.ImportLists
            .AsNoTracking()
            .Where(list => list.Enabled && (list.PlexPlaylist || list.M3uExport))
            .OrderBy(list => list.Id)
            .Select(list => list.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var written = 0;

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A list being synced writes its own playlists at its end; writing here too could create
            // a second Plex playlist before the first has recorded its key.
            using var gate = _gates.TryEnter(id);

            if (gate is null)
            {
                continue;
            }

            if (await _playlists.WriteAsync(id, cancellationToken).ConfigureAwait(false) is { } result)
            {
                LogPlaylistsWritten(_logger, id, result);
                written++;
            }
        }

        return written;
    }

    private static ImportListView View(ImportList list, Dictionary<long, Counts> counts) =>
        counts.TryGetValue(list.Id, out var count)
            ? new ImportListView(list, count.Pending, count.Added, count.Unresolved, count.Skipped, count.Removed)
            : new ImportListView(list, 0, 0, 0, 0, 0);

    private static string NormalisePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return ImportList.AddOnlyPolicy;
        }

        foreach (var known in Policies)
        {
            if (string.Equals(known, policy.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        throw new ImportListValidationException(
            "policy",
            $"policy must be one of {string.Join(", ", Policies)} (was '{policy}').");
    }

    private async Task<Dictionary<long, Counts>> CountsAsync(List<long> ids, CancellationToken cancellationToken)
    {
        var rows = await _database.ImportListItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.ImportListId))
            .GroupBy(item => new { item.ImportListId, item.State, Removed = item.RemovedAt != null })
            .Select(group => new { group.Key.ImportListId, group.Key.State, group.Key.Removed, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = new Dictionary<long, Counts>();

        foreach (var row in rows)
        {
            var count = counts.TryGetValue(row.ImportListId, out var existing) ? existing : new Counts();

            if (row.Removed)
            {
                count = count with { Removed = count.Removed + row.Count };
            }
            else
            {
                count = row.State switch
                {
                    ImportListItemState.Pending => count with { Pending = count.Pending + row.Count },
                    ImportListItemState.Added => count with { Added = count.Added + row.Count },
                    ImportListItemState.Unresolved => count with { Unresolved = count.Unresolved + row.Count },
                    _ => count with { Skipped = count.Skipped + row.Count },
                };
            }

            counts[row.ImportListId] = count;
        }

        return counts;
    }

    /// <summary>
    /// Keeps the stored secrets a masked draft stands for, for the target they were saved for. A
    /// secret whose address changed is refused as a validation failure on that secret's field.
    /// </summary>
    private static string MergeSettings(JsonElement incoming, string? stored, IReadOnlyList<NotificationField> fields)
    {
        try
        {
            return NotificationSecrets.Merge(incoming, stored, fields);
        }
        catch (SecretReentryException exception)
        {
            throw new ImportListValidationException(exception.Field, exception.Message);
        }
    }

    private async Task ApplyAsync(
        ImportList list,
        ImportListDraft draft,
        IImportListProvider provider,
        string? storedSettings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            throw new ImportListValidationException("name", "A list needs a name.");
        }

        if (draft.SyncIntervalHours is < 0 or > 24 * 30)
        {
            throw new ImportListValidationException(
                "syncIntervalHours",
                "syncIntervalHours must be between 0 (only when asked) and 720.");
        }

        var settingsJson = draft.Settings.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => storedSettings ?? "{}",
            JsonValueKind.Object => MergeSettings(draft.Settings, storedSettings, provider.Fields),
            _ => throw new ImportListValidationException("settings", "settings must be a JSON object."),
        };

        var sourceText = draft.SourceText ?? list.SourceText;

        if (sourceText is { Length: > MaxSourceTextLength })
        {
            throw new ImportListValidationException(
                "sourceText",
                $"The file is too large: at most {MaxSourceTextLength / (1024 * 1024)} MB.");
        }

        using (var settings = JsonDocument.Parse(settingsJson))
        {
            var problems = provider.Validate(settings.RootElement, sourceText);

            if (problems.Count > 0)
            {
                throw new ImportListValidationException("settings", string.Join(" ", problems));
            }
        }

        list.Name = draft.Name.Trim();
        list.Settings = settingsJson;
        list.SourceText = sourceText;
        list.Policy = NormalisePolicy(draft.Policy);
        list.Enabled = draft.Enabled;
        list.SyncIntervalHours = draft.SyncIntervalHours;
        // Left out of an update, the playlist outputs keep what they were.
        list.PlexPlaylist = draft.PlexPlaylist ?? list.PlexPlaylist;
        list.M3uExport = draft.M3uExport ?? list.M3uExport;
        list.QualityProfileId = await ProfileIdAsync(draft.QualityProfileId, cancellationToken).ConfigureAwait(false);
        list.LibraryId = await LibraryIdAsync(draft.LibraryId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ProfileIdAsync(long? requested, CancellationToken cancellationToken)
    {
        var id = requested ?? SeedData.StandardProfileId;

        return await _database.QualityProfiles
            .AnyAsync(profile => profile.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ? id
            : throw new ImportListValidationException(
                "qualityProfileId",
                $"Quality profile {id.ToString(CultureInfo.InvariantCulture)} does not exist.");
    }

    private async Task<long> LibraryIdAsync(long? requested, CancellationToken cancellationToken)
    {
        if (requested is { } id)
        {
            return await _database.Libraries
                .AnyAsync(library => library.Id == id, cancellationToken)
                .ConfigureAwait(false)
                ? id
                : throw new ImportListValidationException(
                    "libraryId",
                    $"Library {id.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        var fallback = await _database.Libraries
            .AsNoTracking()
            .OrderByDescending(library => library.IsDefault)
            .ThenBy(library => library.Id)
            .Select(library => (long?)library.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return fallback ?? throw new ImportListValidationException("libraryId", "No library exists yet.");
    }

    /// <summary>
    /// Records what the source holds now: new items pending, known items re-ordered (and back if they
    /// had gone), items no longer there marked removed. Nothing is resolved or deleted here; what
    /// a removal means for a song is the list's policy's business.
    /// </summary>
    /// <remarks>
    /// A read that cannot be trusted to be the whole list (R-007, R-021) adds what it found but marks
    /// nothing removed: it was cut at a provider's cap, or — for a list whose policy removes things — it
    /// returned no rows for a list that held items, or fewer than half the items of the last complete
    /// read of a list of at least <see cref="SuspectMinimumItems"/>. The reason comes back in the diff.
    /// </remarks>
    private async Task<FetchDiff> ApplyFetchAsync(
        ImportList list,
        ImportListFetchResult fetched,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entries = fetched.Entries;
        var items = await _database.ImportListItems
            .Where(item => item.ImportListId == list.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The unique (list, external id) index keeps this to one row per id; the first-row rule is
        // for a database whose duplicates were not cleaned yet.
        var byExternalId = new Dictionary<string, ImportListItem>(StringComparer.Ordinal);
        foreach (var item in items.OrderBy(item => item.Id))
        {
            byExternalId.TryAdd(item.ExternalId, item);
        }

        // What the list held before this read: the last complete read's size, or — for a list that
        // has not had one recorded — the items it still has.
        var heldBefore = list.LastCompleteReadCount > 0
            ? list.LastCompleteReadCount
            : byExternalId.Values.Count(item => item.RemovedAt is null);

        // Songs the policy unmonitored: if somebody has monitored one since, the policy no longer owns
        // that state and a returning item must leave it alone.
        var flaggedSongIds = byExternalId.Values
            .Where(item => item.PolicyUnmonitored && item.SongId is not null)
            .Select(item => item.SongId!.Value)
            .Distinct()
            .ToList();
        var flaggedSongs = flaggedSongIds.Count == 0
            ? []
            : await _database.Songs
                .Where(song => flaggedSongIds.Contains(song.Id))
                .ToDictionaryAsync(song => song.Id, cancellationToken)
                .ConfigureAwait(false);

        foreach (var item in byExternalId.Values.Where(item => item.PolicyUnmonitored))
        {
            if (item.SongId is not { } flaggedId || !flaggedSongs.TryGetValue(flaggedId, out var flagged) || flagged.Monitored)
            {
                item.PolicyUnmonitored = false;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        var added = 0;
        var returned = 0;
        var cut = false;
        var remonitor = new List<ImportListItem>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ExternalId) || !seen.Add(entry.ExternalId))
            {
                continue;
            }

            if (position >= MaxItems)
            {
                cut = true;
                break;
            }

            if (byExternalId.TryGetValue(entry.ExternalId, out var known))
            {
                known.Position = position;

                if (known.RemovedAt is not null)
                {
                    // A returning item (R-019, owner question 13: re-add and re-monitor).
                    returned++;

                    if (known.State == ImportListItemState.Added && known.SongId is null)
                    {
                        // Its song was deleted (Mirror, or the user): look it up and add it again.
                        known.State = ImportListItemState.Pending;
                        known.Reason = null;
                    }
                    else if (known.PolicyUnmonitored && known.SongId is not null)
                    {
                        remonitor.Add(known);
                    }
                }

                known.RemovedAt = null;

                // An item not added yet is looked up again from what the source says now (a
                // retitled track, an ISRC filled in later); an added one keeps the text it was added by.
                if (known.State != ImportListItemState.Added)
                {
                    var raw = ImportListItemJson.Write(entry);

                    if (known.State == ImportListItemState.Unresolved && !string.Equals(raw, known.Raw, StringComparison.Ordinal))
                    {
                        // R-074: text the review screen has not seen is worth another lookup.
                        known.State = ImportListItemState.Pending;
                        known.Reason = null;
                    }

                    known.Raw = raw;
                }
            }
            else
            {
                _database.ImportListItems.Add(new ImportListItem
                {
                    ImportListId = list.Id,
                    ExternalId = entry.ExternalId,
                    Position = position,
                    Raw = ImportListItemJson.Write(entry),
                });
                added++;
            }

            position++;
        }

        var truncated = fetched.Truncated || cut;
        var suspect = SuspectReason(
            list.Policy,
            position,
            truncated,
            cut,
            fetched.ShortRead,
            heldBefore,
            list.SourceEditedSinceRead,
            list.TrustNextRead);

        // A read cut at a cap never marks removals, whatever the policy: the tail it did not reach is
        // not gone. A suspect read marks none either.
        var gone = new List<long>();
        if (!truncated && suspect is null)
        {
            foreach (var item in byExternalId.Values)
            {
                if (!seen.Contains(item.ExternalId) && item.RemovedAt is null)
                {
                    item.RemovedAt = now;
                    gone.Add(item.Id);
                }
            }
        }

        // A returning item: only a song the policy unmonitored and nobody has monitored since.
        foreach (var item in remonitor.Where(item => item.PolicyUnmonitored))
        {
            if (flaggedSongs.TryGetValue(item.SongId!.Value, out var song) && !song.Monitored)
            {
                song.Monitored = true;
            }

            item.PolicyUnmonitored = false;
        }

        list.SuspectReadMessage = suspect;

        if (!truncated && suspect is null)
        {
            list.LastCompleteReadCount = position;
            list.SourceEditedSinceRead = false;
            list.TrustNextRead = false;
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new FetchDiff(position, added, gone.Count, cut, gone, suspect, returned);
    }

    /// <summary>
    /// Why a successful read is not to be trusted with removals, or <see langword="null"/> when it is.
    /// Only a list whose policy removes things (<c>AddAndUnmonitor</c>, <c>Mirror</c>) is held to this:
    /// an add-only list has nothing to lose. A read cut at a cap is not judged here (it removes nothing
    /// whatever the policy), but it is worth saying so on a list that would have removed.
    /// </summary>
    private static string? SuspectReason(
        string policy,
        int read,
        bool truncated,
        bool cut,
        string? shortRead,
        int heldBefore,
        bool sourceEdited,
        bool trusted)
    {
        if (policy == ImportList.AddOnlyPolicy)
        {
            return null;
        }

        if (cut)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The source is longer than Wondarr reads ({MaxItems:N0}), so removals are never applied");
        }

        if (truncated)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Read stopped at the source's cap after {read} items; removals skipped");
        }

        // The user said the next read can be believed, whatever its size (a cap, above, is not a size).
        if (trusted)
        {
            return null;
        }

        // The source announced more than it returned and the gap is not a few unavailable tracks.
        if (shortRead is not null)
        {
            return shortRead + "; removals skipped";
        }

        // After an edit of the source the "under half" rule is off (a smaller playlist is expected);
        // an empty read of a list that still holds items is not.
        var underHalf = !sourceEdited && heldBefore >= SuspectMinimumItems && read * 2 < heldBefore;

        if ((read == 0 && heldBefore > 0) || underHalf)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Read returned {read} of {heldBefore} items; removals skipped");
        }

        return null;
    }

    /// <summary>
    /// What the list's policy does with the songs of the items that just left the source. Only a song
    /// this list added (<c>added_by = list:{id}</c>) that no other item still in any list wants is
    /// touched: "add and unmonitor" unmonitors it; "mirror" deletes it when it has no file and nothing
    /// for it is in the queue, and unmonitors it otherwise. A file is never deleted by a list. A song the
    /// policy unmonitored is remembered on its item (<see cref="ImportListItem.PolicyUnmonitored"/>), so
    /// it is monitored again when the item returns.
    /// </summary>
    private async Task<string> ApplyPolicyAsync(ImportList list, IReadOnlyList<long> goneItemIds, CancellationToken cancellationToken)
    {
        if (goneItemIds.Count == 0 || list.Policy == ImportList.AddOnlyPolicy)
        {
            return string.Empty;
        }

        var addedBy = string.Create(CultureInfo.InvariantCulture, $"list:{list.Id}");
        var goneItems = await _database.ImportListItems
            .Where(item => goneItemIds.Contains(item.Id) && item.SongId != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var songIds = goneItems.Select(item => item.SongId!.Value).Distinct().ToList();

        var unmonitored = 0;
        var deleted = 0;

        // Three lookups for the whole batch, not three per song: a list whose source rotated can
        // drop hundreds of songs at once.
        var songs = await _database.Songs
            .Include(candidate => candidate.File)
            .Where(candidate => songIds.Contains(candidate.Id) && candidate.AddedBy == addedBy)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var wantedElsewhere = (await _database.ImportListItems
                .AsNoTracking()
                .Where(item => item.SongId != null && songIds.Contains(item.SongId.Value) && item.RemovedAt == null)
                .Select(item => item.SongId!.Value)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();

        foreach (var song in songs)
        {
            var songId = song.Id;

            if (wantedElsewhere.Contains(songId))
            {
                continue;
            }

            var outcome = list.Policy == MirrorPolicy && song.File is null
                ? await TryDeleteIdleSongAsync(songId, cancellationToken).ConfigureAwait(false)
                : DeleteOutcome.Busy;

            if (outcome == DeleteOutcome.Gone)
            {
                // Somebody deleted it first: there is nothing left to unmonitor, and the entity is stale.
                continue;
            }

            if (outcome == DeleteOutcome.Deleted)
            {
                deleted++;
            }
            else if (song.Monitored)
            {
                song.Monitored = false;

                foreach (var item in goneItems.Where(item => item.SongId == songId))
                {
                    item.PolicyUnmonitored = true;
                }

                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                unmonitored++;
            }
        }

        return unmonitored + deleted == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"; {unmonitored} unmonitored, {deleted} deleted (no file)");
    }

    /// <summary>
    /// Deletes a song Mirror dropped, but only if nothing is working on it right now. The decision to
    /// delete was made on a snapshot taken before the removals were applied, and a grab or import can
    /// finish in between; the song's queue items go with it, so deleting under them would orphan the
    /// transfer. So the song's file lock is taken (the one an import and a compaction hold), the song
    /// is read again, and it is left alone — and logged — if it now has a file, a queue item that is
    /// not finished, or a compaction move in flight.
    /// </summary>
    /// <returns>Whether the song was deleted, is busy, or was already gone.</returns>
    private async Task<DeleteOutcome> TryDeleteIdleSongAsync(long songId, CancellationToken cancellationToken)
    {
        IAsyncDisposable handle;

        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            wait.CancelAfter(FileLockWait);

            try
            {
                handle = await _fileLock.AcquireAsync(songId, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogDeleteSkipped(_logger, songId, "its file lock is in use");

                return DeleteOutcome.Busy;
            }
        }

        await using (handle.ConfigureAwait(false))
        {
            // Read again, now that nothing can place a file for the song.
            if (!await _database.Songs.AsNoTracking().AnyAsync(candidate => candidate.Id == songId, cancellationToken).ConfigureAwait(false))
            {
                return DeleteOutcome.Gone;
            }

            var hasFile = await _database.SongFiles
                .AsNoTracking()
                .AnyAsync(file => file.SongId == songId, cancellationToken)
                .ConfigureAwait(false);

            if (hasFile)
            {
                LogDeleteSkipped(_logger, songId, "it has a file now");

                return DeleteOutcome.Busy;
            }

            var inFlight = await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(
                    item => item.SongId == songId
                        && item.State != QueueItemState.Imported
                        && item.State != QueueItemState.Failed
                        && item.State != QueueItemState.Cancelled,
                    cancellationToken)
                .ConfigureAwait(false);

            var moving = await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == songId, cancellationToken)
                .ConfigureAwait(false);

            if (inFlight || moving)
            {
                LogDeleteSkipped(_logger, songId, inFlight ? "a grab for it is in flight" : "a compaction has its file");

                return DeleteOutcome.Busy;
            }

            try
            {
                return await _songs.DeleteAsync(songId, cancellationToken).ConfigureAwait(false)
                    ? DeleteOutcome.Deleted
                    : DeleteOutcome.Gone;
            }
            catch (SongsCompactingException)
            {
                // A compaction parked the song's file after the check above.
                LogDeleteSkipped(_logger, songId, "a compaction has its file");

                return DeleteOutcome.Busy;
            }
            catch (SongsBusyException)
            {
                // A grab queued the song after the check above.
                LogDeleteSkipped(_logger, songId, "a grab for it is in flight");

                return DeleteOutcome.Busy;
            }
        }
    }

    private async Task<string> FailAsync(ImportList list, string reason, DateTime now, CancellationToken cancellationToken)
    {
        // The attempt counts as a sync for the schedule: a source that is down is not hammered every
        // hour, and the message says why the list did not change.
        list.LastSyncedAt = now;
        list.LastSyncMessage = "Failed: " + reason;
        list.SuspectReadMessage = "Last read failed: " + reason;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSyncFailed(_logger, list.Id, reason);

        throw new ImportListSyncException(reason);
    }

    private IImportListProvider Provider(string? type) =>
        FindProvider(type)
        ?? throw new ImportListValidationException(
            "type",
            $"type must be one of {string.Join(", ", _providers.Select(provider => provider.Type))} (was '{type}').");

    private IImportListProvider? FindProvider(string? type) =>
        _providers.FirstOrDefault(provider =>
            string.Equals(provider.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Information, Message = "Created import list {ImportListId} of type {Type}")]
    private static partial void LogCreated(ILogger logger, long importListId, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted import list {ImportListId}")]
    private static partial void LogDeleted(ILogger logger, long importListId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced import list {ImportListId}: {Read} items, {New} new, {Gone} gone")]
    private static partial void LogSynced(ILogger logger, long importListId, int read, int @new, int gone);

    [LoggerMessage(Level = LogLevel.Information, Message = "Playlists of import list {ImportListId}: {Result}")]
    private static partial void LogPlaylistsWritten(ILogger logger, long importListId, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import list {ImportListId}: the next read will be trusted whatever its size")]
    private static partial void LogTrustNextRead(ILogger logger, long importListId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import list {ImportListId}: {Reason}")]
    private static partial void LogSuspectRead(ILogger logger, long importListId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mirror left song {SongId} alone: {Why}")]
    private static partial void LogDeleteSkipped(ILogger logger, long songId, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Syncing import list {ImportListId} failed: {Reason}")]
    private static partial void LogSyncFailed(ILogger logger, long importListId, string reason);

    private enum DeleteOutcome
    {
        Deleted,
        Busy,
        Gone,
    }

    private static readonly JsonSerializerOptions SearchBodyJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Whether the source of a list changed between two versions of its settings: its uploaded file, or
    /// any of the provider's non-secret fields. Key order, whitespace and secrets do not count.
    /// </summary>
    private static bool SourceChanged(IImportListProvider provider, string? settingsBefore, string? sourceBefore, ImportList list)
    {
        if (!string.Equals(sourceBefore, list.SourceText, StringComparison.Ordinal))
        {
            return true;
        }

        using var before = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsBefore) ? "{}" : settingsBefore);
        using var after = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings);

        foreach (var field in provider.Fields.Where(field => !field.Secret))
        {
            if (!string.Equals(RawValue(before.RootElement, field.Name), RawValue(after.RootElement, field.Name), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? RawValue(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
                : null;

    private sealed record Counts(int Pending = 0, int Added = 0, int Unresolved = 0, int Skipped = 0, int Removed = 0);

    private sealed record FetchDiff(
        int Read,
        int New,
        int Gone,
        bool Cut,
        IReadOnlyList<long> GoneItemIds,
        string? Suspect,
        int Returned);
}
