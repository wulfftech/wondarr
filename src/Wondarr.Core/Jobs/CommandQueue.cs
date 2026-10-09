using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Persists commands and wakes the executor. A singleton: the in-memory semaphore is what makes
/// "no two rows with the same name are queued or running" hold across concurrent callers, and a
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

            // Lidarr behaviour: a command already queued or running under this name is returned as
            // is, so the executor can never run the same command twice at once.
            var existing = await context.Commands
                .FirstOrDefaultAsync(
                    command => command.Name == name
                        && (command.Status == CommandStatus.Queued || command.Status == CommandStatus.Started),
                    cancellationToken)
                .ConfigureAwait(false);

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

    private static bool HasHandler(IServiceProvider services, string name)
    {
        var lookup = CommandHandlerResolver.Find(services, name);

        // A handler that cannot be built has an unknown name, so it might be this one: queue the
        // command and let the executor fail it with the reason, rather than refusing every command.
        return lookup.Handler is not null || lookup.Failures.Count > 0;
    }
}
