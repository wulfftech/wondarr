using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.ImportLists;

/// <summary>How many lines one pasted list may hold.</summary>
public static class ImportListLimits
{
    /// <summary>The most lines a pasted list accepts.</summary>
    public const int MaxLines = 1000;
}

/// <summary>
/// The counts behind one list, as the review screen shows them.
/// </summary>
/// <param name="ImportListId">The list's id.</param>
/// <param name="Type">The list type, <c>paste</c> for Phase 1.</param>
/// <param name="Name">The list's display name.</param>
/// <param name="Created">The UTC instant the list was created.</param>
/// <param name="LastSyncedAt">The UTC instant the list was last processed, or <see langword="null"/>.</param>
/// <param name="Pending">How many lines have not been looked up yet.</param>
/// <param name="Added">How many lines became songs, including ones the library already held.</param>
/// <param name="Unresolved">How many lines neither provider could identify.</param>
/// <param name="Skipped">How many lines were deliberately not added.</param>
public sealed record ImportListSummary(
    long ImportListId,
    string Type,
    string Name,
    DateTime Created,
    DateTime? LastSyncedAt,
    int Pending,
    int Added,
    int Unresolved,
    int Skipped);

/// <summary>
/// The pasted-list pipeline: text in, a list of lines out, then a background resolve-and-add pass
/// (ARCHITECTURE §5.4, DECISIONS "Build session 2" #6). A line neither provider can identify stays
/// on the list as <see cref="ImportListItemState.Unresolved"/>, with its candidates, until the user
/// picks one or skips it.
/// </summary>
public interface IPasteListService
{
    /// <summary>Stores a pasted list, with one item per line.</summary>
    /// <param name="text">The pasted text: one <c>Artist - Title</c> line, id or link per line.</param>
    /// <param name="qualityProfileId">The profile the list's songs are monitored against, or <see langword="null"/> for the default.</param>
    /// <param name="libraryId">The library the list's songs are filed in, or <see langword="null"/> for the default library.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored list, with its items.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than the limit, or names an unknown profile or library.</exception>
    Task<ImportList> CreateAsync(
        string text,
        long? qualityProfileId,
        long? libraryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves every pending line and adds the songs in one batch, so the album policy sees an
    /// artist's whole run of songs at once.
    /// </summary>
    /// <param name="importListId">The list to process.</param>
    /// <param name="reportProgress">Called after every line with a human-readable progress message.</param>
    /// <param name="cancellationToken">Cancels the work; item states written so far stay.</param>
    /// <returns>A one-line summary of what happened, for the command's completion message.</returns>
    /// <exception cref="ArgumentException">The list does not exist.</exception>
    Task<string> ProcessAsync(
        long importListId,
        Func<string, Task> reportProgress,
        CancellationToken cancellationToken);

    /// <summary>Resolves one unresolved line to a candidate the user picked.</summary>
    /// <param name="itemId">The item to resolve.</param>
    /// <param name="mbRecordingId">The recording MBID to add, or <see langword="null"/>.</param>
    /// <param name="deezerId">The Deezer track id to add, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The resolved item, or <see langword="null"/> when the id is unknown.</returns>
    /// <exception cref="SongNotFoundException">Neither provider knows the id.</exception>
    Task<ImportListItem?> ResolveItemAsync(
        long itemId,
        string? mbRecordingId,
        long? deezerId,
        CancellationToken cancellationToken);

    /// <summary>Marks one line as skipped, so nothing more is done with it.</summary>
    /// <param name="itemId">The item to skip.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The skipped item, or <see langword="null"/> when the id is unknown.</returns>
    Task<ImportListItem?> SkipItemAsync(long itemId, CancellationToken cancellationToken);

    /// <summary>Reads one list's header and its per-state counts, for the review screen.</summary>
    /// <param name="importListId">The list to read.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The summary, or <see langword="null"/> when the id is unknown.</returns>
    Task<ImportListSummary?> GetSummaryAsync(long importListId, CancellationToken cancellationToken);

    /// <summary>Lists the lines of the import lists, filtered and paged.</summary>
    /// <param name="paging">The page, size, sort key and direction.</param>
    /// <param name="importListId">Only lines of this list, or <see langword="null"/> for every list.</param>
    /// <param name="state">Only lines in this state, or <see langword="null"/> for every state.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of lines and the total the filter matched.</returns>
    Task<PagedResult<ImportListItem>> GetItemsAsync(
        PagingSpec paging,
        long? importListId,
        ImportListItemState? state,
        CancellationToken cancellationToken);
}

/// <summary>
/// The pasted-list pipeline. Everything a line produces is saved as it happens, so a restart in the
/// middle of a thousand-line list loses at most the line that was in flight.
/// </summary>
public sealed partial class PasteListService : IPasteListService
{
    /// <summary>The state a line gets when the library already held the song.</summary>
    public const string AlreadyInLibraryReason = "Already in the library";

    /// <summary>The reason a line gets when the user skipped it.</summary>
    public const string SkippedByUserReason = "Skipped by the user";

    /// <summary>How many candidates a line keeps for the review screen.</summary>
    private const int CandidateLimit = 5;

    private readonly WondarrDbContext _database;
    private readonly IIdentityResolver _resolver;
    private readonly ISongService _songs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PasteListService> _logger;

    /// <summary>Initialises a new instance of the <see cref="PasteListService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="resolver">The identity resolver behind every line.</param>
    /// <param name="songs">The song service, for the one batch and the single-line resolve.</param>
    /// <param name="timeProvider">The clock, for the list's name and its last-synced instant.</param>
    /// <param name="logger">The logger.</param>
    public PasteListService(
        WondarrDbContext database,
        IIdentityResolver resolver,
        ISongService songs,
        TimeProvider timeProvider,
        ILogger<PasteListService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _resolver = resolver;
        _songs = songs;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ImportList> CreateAsync(
        string text,
        long? qualityProfileId,
        long? libraryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = SplitLines(text);

        if (lines.Count == 0)
        {
            throw new ArgumentException("The pasted list has no lines.", nameof(text));
        }

        if (lines.Count > ImportListLimits.MaxLines)
        {
            throw new ArgumentException(
                $"A pasted list holds at most {ImportListLimits.MaxLines.ToString(CultureInfo.InvariantCulture)} lines.",
                nameof(text));
        }

        var profile = qualityProfileId ?? SeedData.StandardProfileId;
        if (!await _database.QualityProfiles
            .AnyAsync(candidate => candidate.Id == profile, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Quality profile {profile.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(qualityProfileId));
        }

        var library = libraryId ?? SeedData.DefaultLibraryId;
        if (!await _database.Libraries
            .AnyAsync(candidate => candidate.Id == library, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Library {library.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(libraryId));
        }

        var list = new ImportList
        {
            Type = ImportList.PasteType,
            Name = ListName(_timeProvider.GetUtcNow().UtcDateTime),
            QualityProfileId = profile,
            LibraryId = library,
        };

        // A line that repeats an earlier one — whatever its case — would only produce a duplicate
        // song, so it is recorded as skipped and named by the line it repeats.
        var firstByLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var number = index + 1;
            var item = new ImportListItem
            {
                ExternalId = number.ToString(CultureInfo.InvariantCulture),
                Raw = ImportListItemJson.Write(ImportListItemJson.Parse(line)),
            };

            if (firstByLine.TryGetValue(line, out var first))
            {
                item.State = ImportListItemState.Skipped;
                item.Reason = $"Duplicate of line {first.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                firstByLine.Add(line, number);
            }

            list.Items.Add(item);
        }

        _database.ImportLists.Add(list);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(_logger, list.Id, list.Items.Count);

        return list;
    }

    /// <inheritdoc />
    public async Task<string> ProcessAsync(
        long importListId,
        Func<string, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == importListId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"Import list {importListId.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(importListId));

        // Items are written in line order, so their ids order them by line without a numeric column.
        var items = await _database.ImportListItems
            .Where(item => item.ImportListId == importListId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pending = items.Where(item => item.State == ImportListItemState.Pending).ToList();
        var identities = new List<SongIdentity>(pending.Count);
        var resolvedItems = new List<ImportListItem>(pending.Count);
        var done = 0;

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ResolveLineAsync(item, identities, resolvedItems, cancellationToken).ConfigureAwait(false);

            // Saved line by line: a restart mid-list re-processes only the lines still pending.
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            done++;
            await reportProgress(
                $"Resolved {done.ToString(CultureInfo.InvariantCulture)} of {pending.Count.ToString(CultureInfo.InvariantCulture)} lines")
                .ConfigureAwait(false);
        }

        if (identities.Count > 0)
        {
            // One batch, so the album policy groups an artist's songs instead of scattering singles.
            var results = await _songs
                .AddIdentitiesAsync(identities, OptionsFor(list), cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < results.Count; index++)
            {
                var item = resolvedItems[index];
                var result = results[index];

                item.State = ImportListItemState.Added;
                item.SongId = result.Song.Id;
                item.Reason = result.Outcome == SongAddOutcome.AlreadyExists ? AlreadyInLibraryReason : null;
            }
        }

        list.LastSyncedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var summary = Summarize(items);

        LogProcessed(_logger, list.Id, summary.Total, summary.Added, summary.Unresolved, summary.Skipped);

        return SummaryLine(summary);
    }

    /// <inheritdoc />
    public async Task<ImportListItem?> ResolveItemAsync(
        long itemId,
        string? mbRecordingId,
        long? deezerId,
        CancellationToken cancellationToken)
    {
        var item = await _database.ImportListItems
            .FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        var list = await _database.ImportLists
            .FirstAsync(candidate => candidate.Id == item.ImportListId, cancellationToken)
            .ConfigureAwait(false);

        // The add itself decides what it is: an unknown id throws, and the caller turns that into a 404.
        var result = await _songs
            .AddAsync(mbRecordingId, deezerId, OptionsFor(list), cancellationToken)
            .ConfigureAwait(false);

        item.State = ImportListItemState.Added;
        item.SongId = result.Song.Id;
        item.Reason = null;

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return item;
    }

    /// <inheritdoc />
    public async Task<ImportListItem?> SkipItemAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await _database.ImportListItems
            .FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        item.State = ImportListItemState.Skipped;
        item.Reason = SkippedByUserReason;

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return item;
    }

    /// <inheritdoc />
    public async Task<ImportListSummary?> GetSummaryAsync(long importListId, CancellationToken cancellationToken)
    {
        var list = await _database.ImportLists
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == importListId, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return null;
        }

        var counts = await _database.ImportListItems
            .AsNoTracking()
            .Where(item => item.ImportListId == importListId)
            .GroupBy(item => item.State)
            .Select(group => new StateCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ImportListSummary(
            list.Id,
            list.Type,
            list.Name,
            list.CreatedAt,
            list.LastSyncedAt,
            Count(counts, ImportListItemState.Pending),
            Count(counts, ImportListItemState.Added),
            Count(counts, ImportListItemState.Unresolved),
            Count(counts, ImportListItemState.Skipped));
    }

    /// <inheritdoc />
    public async Task<PagedResult<ImportListItem>> GetItemsAsync(
        PagingSpec paging,
        long? importListId,
        ImportListItemState? state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.ImportListItems.AsNoTracking();

        if (importListId is { } id)
        {
            query = query.Where(item => item.ImportListId == id);
        }

        if (state is { } wanted)
        {
            query = query.Where(item => item.State == wanted);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ImportListItem>(records, totalRecords);
    }

    /// <summary>
    /// Resolves one pending line and writes what came of it. A provider that fails on one line
    /// leaves that line unresolved rather than failing the whole list.
    /// </summary>
    private async Task ResolveLineAsync(
        ImportListItem item,
        List<SongIdentity> identities,
        List<ImportListItem> resolvedItems,
        CancellationToken cancellationToken)
    {
        var line = ImportListItemJson.ReadLine(item).Line;

        try
        {
            var result = await _resolver.ResolveAsync(line, cancellationToken).ConfigureAwait(false);

            switch (result.Status)
            {
                case ResolveStatus.Resolved:
                case ResolveStatus.ResolvedDeezerOnly:
                    // The line stays pending until the batch has run: it is only added then.
                    identities.Add(result.Identity!);
                    resolvedItems.Add(item);
                    break;

                case ResolveStatus.Unsupported:
                    item.State = ImportListItemState.Skipped;
                    item.Reason = result.Reason ?? $"'{line}' cannot be looked up.";
                    break;

                default:
                    item.State = ImportListItemState.Unresolved;
                    item.Reason = result.Reason ?? $"No match for '{line}'.";
                    item.Candidates = ImportListItemJson.Write(
                        result.Candidates.Take(CandidateLimit).Select(Stored).ToList());
                    break;
            }
        }
        catch (MetadataProviderException exception)
        {
            Fail(item, exception);
        }
        catch (HttpRequestException exception)
        {
            Fail(item, exception);
        }

        static void Fail(ImportListItem item, Exception exception)
        {
            item.State = ImportListItemState.Unresolved;
            item.Reason = $"Lookup failed: {exception.Message}";
        }
    }

    /// <summary>What every song added by this list records as its origin, and where it lands.</summary>
    private static SongAddOptions OptionsFor(ImportList list) => new()
    {
        QualityProfileId = list.QualityProfileId,
        LibraryId = list.LibraryId,
        AddedBy = $"list:{list.Id.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>
    /// The counts of one run, as the summary line and the completion message read them. "Added" splits
    /// into songs the run created and songs the library already held.
    /// </summary>
    private static LineCounts Summarize(List<ImportListItem> items)
    {
        var added = 0;
        var existing = 0;
        var unresolved = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            switch (item.State)
            {
                case ImportListItemState.Added when item.Reason == AlreadyInLibraryReason:
                    existing++;
                    break;
                case ImportListItemState.Added:
                    added++;
                    break;
                case ImportListItemState.Unresolved:
                    unresolved++;
                    break;
                case ImportListItemState.Skipped:
                    skipped++;
                    break;
                default:
                    break;
            }
        }

        return new LineCounts(items.Count, added, existing, unresolved, skipped);
    }

    /// <summary>The one-line result of a run, as the command's completion message.</summary>
    private static string SummaryLine(LineCounts counts) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{counts.Total} lines: {counts.Added} added, {counts.Existing} already in the library, {counts.Unresolved} unresolved, {counts.Skipped} skipped");

    /// <summary>The candidates of an item, as stored.</summary>
    private static ImportListCandidate Stored(SongCandidate candidate) => new(
        candidate.Source,
        candidate.MbRecordingId,
        candidate.DeezerId,
        candidate.Title,
        candidate.ArtistCredit,
        candidate.DurationMs,
        candidate.Score);

    /// <summary>Sort keys are case-insensitive; no key at all is the line order the user pasted.</summary>
    private static IQueryable<ImportListItem> ApplySort(IQueryable<ImportListItem> query, PagingSpec paging) =>
        paging.SortKey?.ToLowerInvariant() switch
        {
            "added" => By(query, item => item.CreatedAt, paging.Descending),
            "line" => By(query, item => item.Id, paging.Descending),
            _ => By(query, item => item.Id, descending: false),
        };

    private static IQueryable<ImportListItem> By<TKey>(
        IQueryable<ImportListItem> query,
        Expression<Func<ImportListItem, TKey>> key,
        bool descending)
    {
        var ordered = descending ? query.OrderByDescending(key) : query.OrderBy(key);

        return ordered.ThenBy(item => item.Id);
    }

    /// <summary>The pasted text as lines: blank lines and comments are dropped, the rest trimmed.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length > 0 && !line.StartsWith('#'))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>The list's display name, from the injected clock so tests can pin it.</summary>
    private static string ListName(DateTime now) =>
        $"Pasted list {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";

    private static int Count(IReadOnlyList<StateCount> counts, ImportListItemState state)
    {
        foreach (var entry in counts)
        {
            if (entry.State == state)
            {
                return entry.Count;
            }
        }

        return 0;
    }

    /// <summary>How many lines of one list are in one state.</summary>
    private sealed record StateCount(ImportListItemState State, int Count);

    /// <summary>What one processing run did with a list's lines.</summary>
    private sealed record LineCounts(int Total, int Added, int Existing, int Unresolved, int Skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored import list {ImportListId} with {LineCount} lines")]
    private static partial void LogCreated(ILogger logger, long importListId, int lineCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processed import list {ImportListId}: {LineCount} lines, {Added} added, {Unresolved} unresolved, {Skipped} skipped")]
    private static partial void LogProcessed(
        ILogger logger,
        long importListId,
        int lineCount,
        int added,
        int unresolved,
        int skipped);
}

/// <summary>
/// The JSON columns of an import-list item: the line as pasted and the candidates of an unresolved
/// one. Both are camelCase, so the columns read like the API's own resources.
/// </summary>
public static class ImportListItemJson
{
    /// <summary>Everything stored on a line is camelCase, so the columns read like the API's.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The stored shape of one line, with the artist and title a lookup parsed out of it.</summary>
    /// <param name="line">The trimmed line.</param>
    /// <returns>The line, and what parsing it into <c>Artist - Title</c> produced.</returns>
    public static ImportListLine Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var input = LookupInput.Parse(line);

        return input.Kind == LookupKind.ArtistTitle
            ? new ImportListLine(line, input.Artist, input.Title)
            : new ImportListLine(line, null, null);
    }

    /// <summary>Serializes a line for its <c>raw</c> column.</summary>
    /// <param name="line">The line to store.</param>
    public static string Write(ImportListLine line) => JsonSerializer.Serialize(line, StoredJson);

    /// <summary>Serializes candidates for an item's <c>candidates</c> column.</summary>
    /// <param name="candidates">The candidates to store.</param>
    public static string Write(IReadOnlyCollection<ImportListCandidate> candidates) =>
        JsonSerializer.Serialize(candidates, StoredJson);

    /// <summary>Reads the line text and the artist and title parsed out of it.</summary>
    /// <param name="item">The item to read.</param>
    /// <returns>The stored line; the raw JSON when it cannot be read.</returns>
    public static ImportListLine ReadLine(ImportListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return JsonSerializer.Deserialize<ImportListLine>(item.Raw, StoredJson)
            ?? new ImportListLine(item.Raw, null, null);
    }

    /// <summary>Reads the candidates of an unresolved line.</summary>
    /// <param name="item">The item to read.</param>
    /// <returns>The candidates, empty when the line has none.</returns>
    public static IReadOnlyList<ImportListCandidate> ReadCandidates(ImportListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return JsonSerializer.Deserialize<List<ImportListCandidate>>(item.Candidates, StoredJson) ?? [];
    }
}
