using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;

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
public sealed record ImportListDraft(
    string Type,
    string Name,
    JsonElement Settings,
    string? SourceText,
    string? Policy,
    long? QualityProfileId,
    long? LibraryId,
    bool Enabled = true,
    int SyncIntervalHours = 24);

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

    /// <summary>The ids of the enabled synced lists whose interval has passed, oldest sync first.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<long>> GetDueAsync(CancellationToken cancellationToken);
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

    /// <summary>The longest uploaded file, in characters (about 5 MB of CSV).</summary>
    public const int MaxSourceTextLength = 5 * 1024 * 1024;

    /// <summary>
    /// The policies a list may name (DECISIONS build session 7 #9). Only "add only" is offered until
    /// the sync applies the others (P6-03): a list must never claim a policy it does not carry out.
    /// </summary>
    public static readonly IReadOnlyList<string> Policies = [ImportList.AddOnlyPolicy];

    private readonly WondarrDbContext _database;
    private readonly IPasteListService _items;
    private readonly IReadOnlyList<IImportListProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportListService> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportListService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="items">The item pipeline the bulk add uses: resolve, then one batch add.</param>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ImportListService(
        WondarrDbContext database,
        IPasteListService items,
        IEnumerable<IImportListProvider> providers,
        TimeProvider timeProvider,
        ILogger<ImportListService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _items = items;
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

        await ApplyAsync(list, draft, Provider(list.Type), list.Settings, cancellationToken).ConfigureAwait(false);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

        var diff = await ApplyFetchAsync(list, fetched.Entries, now, cancellationToken).ConfigureAwait(false);

        // The same pipeline a pasted list runs: pending items are resolved one by one, then added in
        // one batch so the album policy sees an artist's songs together and one search is queued.
        var processed = await _items.ProcessAsync(list.Id, reportProgress, cancellationToken).ConfigureAwait(false);

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Read {diff.Read} items ({diff.New} new, {diff.Gone} no longer in the list{(diff.Cut ? $", cut at {MaxItems}" : string.Empty)}); {processed}");

        list.LastSyncedAt = now;
        list.LastSyncMessage = message;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSynced(_logger, list.Id, diff.Read, diff.New, diff.Gone);

        return message;
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
            JsonValueKind.Object => NotificationSecrets.Merge(draft.Settings, storedSettings, provider.Fields),
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
    /// a removal means for a song is the list's policy's business (DECISIONS build session 7 #9).
    /// </summary>
    private async Task<FetchDiff> ApplyFetchAsync(
        ImportList list,
        IReadOnlyList<ImportListEntry> entries,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var items = await _database.ImportListItems
            .Where(item => item.ImportListId == list.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A list written before external ids were unique keeps its first row per id.
        var byExternalId = new Dictionary<string, ImportListItem>(StringComparer.Ordinal);
        foreach (var item in items.OrderBy(item => item.Id))
        {
            byExternalId.TryAdd(item.ExternalId, item);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        var added = 0;
        var cut = false;

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
                known.RemovedAt = null;

                // An item not added yet is looked up again from what the source says now (a
                // retitled track, an ISRC filled in later); an added one keeps the text it was added by.
                if (known.State != ImportListItemState.Added)
                {
                    known.Raw = ImportListItemJson.Write(entry);
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

        var gone = 0;
        foreach (var item in byExternalId.Values)
        {
            if (!seen.Contains(item.ExternalId) && item.RemovedAt is null)
            {
                item.RemovedAt = now;
                gone++;
            }
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new FetchDiff(position, added, gone, cut);
    }

    private async Task<string> FailAsync(ImportList list, string reason, DateTime now, CancellationToken cancellationToken)
    {
        // The attempt counts as a sync for the schedule: a source that is down is not hammered every
        // hour, and the message says why the list did not change.
        list.LastSyncedAt = now;
        list.LastSyncMessage = "Failed: " + reason;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Syncing import list {ImportListId} failed: {Reason}")]
    private static partial void LogSyncFailed(ILogger logger, long importListId, string reason);

    private sealed record Counts(int Pending = 0, int Added = 0, int Unresolved = 0, int Skipped = 0, int Removed = 0);

    private sealed record FetchDiff(int Read, int New, int Gone, bool Cut);
}
