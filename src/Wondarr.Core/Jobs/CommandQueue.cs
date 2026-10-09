using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<long> _queue;
    private readonly IEventAggregator _eventAggregator;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initialises a new instance of the <see cref="CommandQueue"/> class.</summary>
    public CommandQueue(
        IServiceScopeFactory scopeFactory,
        Channel<long> queue,
        IEventAggregator eventAggregator,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _queue = queue;
        _eventAggregator = eventAggregator;
        _timeProvider = timeProvider;
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

            if (!HasHandler(scope.ServiceProvider, name))
            {
                throw new UnknownCommandException(name);
            }

            // Lidarr behaviour: a command already queued or running with this name and body is returned
            // as is, so the executor can never run the same command twice at once. The body counts: a
            // scan of reference library 2 is not the scan of library 1 that happens to be running.
            var active = await context.Commands
                .Where(command => command.Name == name
                    && (command.Status == CommandStatus.Queued || command.Status == CommandStatus.Started))
                .OrderBy(command => command.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existing = active.Find(command => SameBody(name, command.Body, body));

            if (existing is not null)
            {
                return existing;
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
        _gate.Dispose();
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

            var record = await context.Commands
                .FirstOrDefaultAsync(command => command.Id == id, cancellationToken)
                .ConfigureAwait(false);

            // Only a command that has not started can be cancelled; anything else is reported as a
            // conflict so the caller does not believe it stopped something that is running.
            if (record is null || record.Status != CommandStatus.Queued)
            {
                return false;
            }

            record.Status = CommandStatus.Cancelled;
            record.Result = CommandResult.Unsuccessful;
            record.EndedAt = _timeProvider.GetUtcNow().UtcDateTime;
            record.Message = "Cancelled";

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

    private static bool HasHandler(IServiceProvider services, string name)
    {
        var lookup = CommandHandlerResolver.Find(services, name);
        lookup.ReleaseAsync().AsTask().GetAwaiter().GetResult();

        // A handler that cannot be built has an unknown name, so it might be this one: queue the
        // command and let the executor fail it with the reason, rather than refusing every command.
        return lookup.Handler is not null || lookup.Failures.Count > 0;
    }
}
