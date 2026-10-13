using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Persists commands and wakes the executor. A singleton: the in-memory semaphore is what makes
/// "no two rows with the same name and body are queued or running" hold across concurrent callers, and a
/// second instance would break that.
/// </summary>
// The name is fixed by the task spec (it is the *arr vocabulary) and is not a System.Collections.Queue.
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the task spec; this is a command queue, not a collection.")]
public sealed class CommandQueue : ICommandQueue, IDisposable
{
    private const string StrandedMessage = "Stranded: the executor lost track of this command, so it was failed.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<long> _queue;
    private readonly IEventAggregator _eventAggregator;
    private readonly TimeProvider _timeProvider;
    private readonly ActiveCommands _activeCommands;
    private readonly SemaphoreSlim _gate;

    /// <summary>Initialises a new instance of the <see cref="CommandQueue"/> class.</summary>
    public CommandQueue(
        IServiceScopeFactory scopeFactory,
        Channel<long> queue,
        IEventAggregator eventAggregator,
        TimeProvider timeProvider,
        ActiveCommands activeCommands)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(activeCommands);

        _scopeFactory = scopeFactory;
        _queue = queue;
        _eventAggregator = eventAggregator;
        _timeProvider = timeProvider;
        _activeCommands = activeCommands;
        _gate = activeCommands.Gate;
    }

    /// <inheritdoc />
    public async Task<CommandRecord> EnqueueAsync(
        string name,
        string? body,
        CommandTrigger trigger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            // The handler's own name is the canonical one: the resolver matches names case-insensitively,
            // so "missingsearch" and "MissingSearch" are the same command and must dedupe as one.
            var canonical = ResolveName(scope.ServiceProvider, name) ?? throw new UnknownCommandException(name);
            name = canonical;

            // Lidarr behaviour: a command already queued or running with this name and body is returned
            // as is, so the executor can never run the same command twice at once. The body counts: a
            // scan of reference library 2 is not the scan of library 1 that happens to be running.
            // Only a handful of rows are ever Queued or Started, so the name is compared in memory,
            // without regard to case: a row an older build wrote under another spelling counts too.
            // Which rows the process holds is taken before the rows are read: a command that finishes in
            // between was held when the snapshot was taken, so its fresh Completed row is never taken for
            // a stranded one.
            var held = _activeCommands.Snapshot();
            var running = await context.Commands
                .Where(command => command.Status == CommandStatus.Queued || command.Status == CommandStatus.Started)
                .OrderBy(command => command.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var active = running
                .Where(command => string.Equals(command.Name, canonical, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A row the executor no longer holds in memory will never be finished: a failed status write
            // stranded it. Fail it, so it cannot swallow this enqueue and every one after it.
            var stranded = active.Where(command => !held.Contains(command.Id)).ToList();
            var strandedAt = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var command in stranded)
            {
                active.Remove(command);

                // Conditional, so a row that was finished after it was read is left as it is.
                var changed = await context.Commands
                    .Where(row => row.Id == command.Id
                        && (row.Status == CommandStatus.Queued || row.Status == CommandStatus.Started))
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(row => row.Status, CommandStatus.Failed)
                            .SetProperty(row => row.Result, CommandResult.Unsuccessful)
                            .SetProperty(row => row.EndedAt, (DateTime?)strandedAt)
                            .SetProperty(row => row.Message, StrandedMessage)
                            .SetProperty(row => row.Exception, StrandedMessage)
                            .SetProperty(row => row.UpdatedAt, strandedAt),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (changed > 0)
                {
                    var failed = await context.Commands
                        .AsNoTracking()
                        .FirstAsync(row => row.Id == command.Id, cancellationToken)
                        .ConfigureAwait(false);

                    await _eventAggregator
                        .PublishAsync(new CommandUpdatedEvent(failed), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var existing = active.Find(command => SameBody(name, command.Body, body));

            if (existing is not null)
            {
                return existing;
            }

            // A request to search particular songs, while another such request has not started yet, is
            // folded into it: one command with all the songs, not a pile of them behind a long run.
            var merged = await TryMergeQueuedAsync(context, name, active, body, cancellationToken).ConfigureAwait(false);

            if (merged is not null)
            {
                return merged;
            }

            var record = new CommandRecord
            {
                Name = name,
                Body = body,
                Status = CommandStatus.Queued,
                Result = CommandResult.Unknown,
                Trigger = trigger,
                QueuedAt = _timeProvider.GetUtcNow().UtcDateTime,
            };

            context.Commands.Add(record);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _activeCommands.Add(record.Id);

            // Publish before waking the executor: whoever is listening sees "queued" come first.
            await _eventAggregator
                .PublishAsync(new CommandUpdatedEvent(record), cancellationToken)
                .ConfigureAwait(false);

            _queue.Writer.TryWrite(record.Id);

            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<CommandRecord?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await context.Commands
            .AsNoTracking()
            .FirstOrDefaultAsync(command => command.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CommandRecord>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await context.Commands
            .AsNoTracking()
            .OrderByDescending(command => command.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(long id, CancellationToken cancellationToken)
    {
        CommandRecord? cancelled = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            // One conditional update, so a cancel and the executor starting the same row cannot both win:
            // whichever statement SQLite runs first changes the status, and the other finds no Queued row.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var changed = await context.Commands
                .Where(command => command.Id == id && command.Status == CommandStatus.Queued)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(command => command.Status, CommandStatus.Cancelled)
                        .SetProperty(command => command.Result, CommandResult.Unsuccessful)
                        .SetProperty(command => command.EndedAt, (DateTime?)now)
                        .SetProperty(command => command.Message, "Cancelled")
                        .SetProperty(command => command.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);

            // Only a command that has not started can be cancelled; anything else is reported as a
            // conflict so the caller does not believe it stopped something that is running.
            if (changed == 0)
            {
                return false;
            }

            _activeCommands.Remove(id);

            var record = await context.Commands
                .AsNoTracking()
                .FirstAsync(command => command.Id == id, cancellationToken)
                .ConfigureAwait(false);

            cancelled = record;
        }
        finally
        {
            _gate.Release();
        }

        await _eventAggregator
            .PublishAsync(new CommandUpdatedEvent(cancelled), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Adds the <c>songIds</c> of <paramref name="body"/> to a Queued command whose body also names
    /// <c>songIds</c> (the ids sorted, without repeats), when there is one. The update is conditional on
    /// the command still being Queued, so a command the executor has just started is never edited.
    /// </summary>
    private async Task<CommandRecord?> TryMergeQueuedAsync(
        WondarrDbContext context,
        string name,
        List<CommandRecord> active,
        string? body,
        CancellationToken cancellationToken)
    {
        // Only the missing search is folded: it is a request to look at these songs, and two of them are
        // one request for the union. Other commands that name songs (MoveSongs, ConvertFiles) carry
        // parameters of their own, and two of them are two different requests.
        if (!string.Equals(name, MissingSearchCommandHandler.CommandName, StringComparison.Ordinal)
            || ReadSongIds(body) is not { } wanted)
        {
            return null;
        }

        foreach (var queued in active.Where(command => command.Status == CommandStatus.Queued))
        {
            if (ReadSongIds(queued.Body) is not { } already || JsonNode.Parse(queued.Body!) is not JsonObject node)
            {
                continue;
            }

            // Every other member of the queued body is kept as it is.
            foreach (var key in node.Select(member => member.Key).Where(key => string.Equals(key, "songIds", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                node.Remove(key);
            }

            node["songIds"] = new JsonArray([.. already.Union(wanted).Order().Select(id => (JsonNode?)JsonValue.Create(id))]);
            var merged = node.ToJsonString();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            var changed = await context.Commands
                .Where(row => row.Id == queued.Id && row.Status == CommandStatus.Queued)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.Body, merged)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);

            if (changed > 0)
            {
                return await context.Commands
                    .AsNoTracking()
                    .FirstAsync(row => row.Id == queued.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>The <c>songIds</c> a body names, or <see langword="null"/> when it names none.</summary>
    private static List<long>? ReadSongIds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (string.Equals(member.Name, "songIds", StringComparison.OrdinalIgnoreCase)
                    && member.Value.ValueKind == JsonValueKind.Array)
                {
                    return [.. member.Value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _))
                        .Select(item => item.GetInt64())];
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Whether two command bodies ask for the same thing: equal as JSON values, so whitespace and key
    /// order do not matter, and an empty body equals a body that only repeats the command's own name
    /// (<c>{"name":"MissingSearch"}</c> is what the API sends for what a scheduled task enqueues with
    /// none). A body that is not JSON is compared as text.
    /// </summary>
    private static bool SameBody(string name, string? left, string? right)
    {
        var first = Normalise(name, left);
        var second = Normalise(name, right);

        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        try
        {
            using var firstDocument = JsonDocument.Parse(first);
            using var secondDocument = JsonDocument.Parse(second);

            return JsonEquals(firstDocument.RootElement, secondDocument.RootElement);
        }
        catch (JsonException)
        {
            return string.Equals(first, second, StringComparison.Ordinal);
        }
    }

    /// <summary>The body, or <see langword="null"/> when it says nothing the command's name does not.</summary>
    private static string? Normalise(string name, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            // Handlers read their bodies case-insensitively and treat null as absent, so so does this.
            var onlyTheName = Members(root).All(property =>
                string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && string.Equals(property.Value.GetString(), name, StringComparison.Ordinal));

            return onlyTheName ? null : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    /// <summary>The members of a JSON object whose value is not null: a null member is the same as an absent one.</summary>
    private static IEnumerable<JsonProperty> Members(JsonElement element) =>
        element.EnumerateObject().Where(member => member.Value.ValueKind != JsonValueKind.Null);

    /// <summary>Structural equality of two JSON values: object members by name, array items in order.</summary>
    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftMembers = Members(left).ToList();
                var rightMembers = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);

                foreach (var member in Members(right))
                {
                    rightMembers[member.Name] = member;
                }

                return leftMembers.Count == rightMembers.Count
                    && leftMembers.TrueForAll(member =>
                        rightMembers.TryGetValue(member.Name, out var other) && JsonEquals(member.Value, other.Value));

            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();

                return leftItems.Count == rightItems.Count
                    && leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber)
                    ? leftNumber == rightNumber
                    : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            default:
                // true, false and null carry no payload beyond their kind.
                return true;
        }
    }

    /// <summary>The handler's own spelling of the name, or <see langword="null"/> when nothing answers to it.</summary>
    private static string? ResolveName(IServiceProvider services, string name)
    {
        var lookup = CommandHandlerResolver.Find(services, name);
        var canonical = lookup.Handler?.Name;
        lookup.ReleaseAsync().AsTask().GetAwaiter().GetResult();

        // A handler that cannot be built has an unknown name, so it might be this one: queue the
        // command as given and let the executor fail it with the reason, rather than refusing every command.
        return canonical ?? (lookup.Failures.Count > 0 ? name : null);
    }
}
