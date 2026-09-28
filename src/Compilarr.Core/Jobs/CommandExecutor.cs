using System.Threading.Channels;
using Compilarr.Core.Messaging;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Jobs;

/// <summary>
/// Runs queued commands. The queue hands out ids over a channel and this service drains it with a
/// fixed set of workers, so no polling is needed and the concurrency limit is simply the number of
/// workers.
/// </summary>
public sealed partial class CommandExecutor : BackgroundService
{
    /// <summary>How many commands may run at the same time.</summary>
    public const int MaxConcurrency = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventAggregator _eventAggregator;
    private readonly ChannelReader<long> _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CommandExecutor> _logger;

    // Every status write is serialised: SQLite has one writer at a time, and three workers racing
    // for it would turn into "database is locked" under load.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Initialises a new instance of the <see cref="CommandExecutor"/> class.</summary>
    public CommandExecutor(
        IServiceScopeFactory scopeFactory,
        IEventAggregator eventAggregator,
        Channel<long> queue,
        TimeProvider timeProvider,
        ILogger<CommandExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _eventAggregator = eventAggregator;
        _queue = queue.Reader;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _writeGate.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    /// <summary>
    /// Orphans what a previous process left behind <em>before</em> reporting started. Doing it in
    /// <see cref="ExecuteAsync"/> raced: <c>BackgroundService.StartAsync</c> returns at the first
    /// await, so a command queued in that window was swept up as orphaned and never ran.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await OrphanInterruptedCommandsAsync().ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Task[MaxConcurrency];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = RunWorkerAsync(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAsync(id, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: anything in flight has already been marked aborted by RunAsync.
        }
    }

    private async Task RunAsync(long id, CancellationToken stoppingToken)
    {
        var started = await MarkStartedAsync(id).ConfigureAwait(false);
        if (started is null)
        {
            // Cancelled, or already handled by another worker.
            return;
        }

        await _eventAggregator
            .PublishAsync(new CommandUpdatedEvent(started), stoppingToken)
            .ConfigureAwait(false);

        string? message = null;
        string? error = null;
        var aborted = false;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices<ICommandHandler>()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, started.Name, StringComparison.OrdinalIgnoreCase));

            if (handler is null)
            {
                error = $"No handler is registered for command '{started.Name}'.";
            }
            else
            {
                var context = new CommandContext(
                    started.Id,
                    started.Body,
                    started.Trigger,
                    progress => ReportProgressAsync(started.Id, progress));

                message = await handler.ExecuteAsync(context, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            aborted = true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            LogCommandFailed(started.Id, started.Name, exception);
        }

        var finished = await MarkFinishedAsync(id, aborted, message, error).ConfigureAwait(false);
        if (finished is not null)
        {
            await _eventAggregator
                .PublishAsync(new CommandUpdatedEvent(finished), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task ReportProgressAsync(long id, string message)
    {
        var updated = await UpdateAsync(
            id,
            record =>
            {
                record.Message = message;

                return true;
            }).ConfigureAwait(false);
        if (updated is not null)
        {
            await _eventAggregator
                .PublishAsync(new CommandUpdatedEvent(updated), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task OrphanInterruptedCommandsAsync()
    {
        List<CommandRecord> orphaned;

        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

            orphaned = await context.Commands
                .Where(command => command.Status == CommandStatus.Queued || command.Status == CommandStatus.Started)
                .ToListAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (orphaned.Count == 0)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var record in orphaned)
            {
                record.Status = CommandStatus.Orphaned;
                record.EndedAt = now;
            }

            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        LogOrphaned(orphaned.Count);

        foreach (var record in orphaned)
        {
            await _eventAggregator
                .PublishAsync(new CommandUpdatedEvent(record), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<CommandRecord?> MarkStartedAsync(long id)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return await UpdateAsync(
            id,
            record =>
            {
                if (record.Status != CommandStatus.Queued)
                {
                    // Cancelled while it waited, or another worker got there first.
                    return false;
                }

                record.Status = CommandStatus.Started;
                record.StartedAt = now;
                record.Result = CommandResult.Unknown;

                return true;
            }).ConfigureAwait(false);
    }

    private async Task<CommandRecord?> MarkFinishedAsync(long id, bool aborted, string? message, string? error)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return await UpdateAsync(
            id,
            record =>
            {
                if (record.Status != CommandStatus.Started)
                {
                    return false;
                }

                record.Status = aborted
                    ? CommandStatus.Aborted
                    : error is null ? CommandStatus.Completed : CommandStatus.Failed;
                record.Result = error is null && !aborted ? CommandResult.Successful : CommandResult.Unsuccessful;
                record.Message = message ?? record.Message;
                record.Exception = error;
                record.EndedAt = now;

                return true;
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the command and saves whatever <paramref name="mutate"/> changed, returning the changed
    /// row — or <see langword="null"/> when the row is gone or the mutation declined to apply.
    /// </summary>
    private async Task<CommandRecord?> UpdateAsync(long id, Func<CommandRecord, bool> mutate)
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

            var record = await context.Commands
                .FirstOrDefaultAsync(command => command.Id == id, CancellationToken.None)
                .ConfigureAwait(false);

            if (record is null || !mutate(record))
            {
                return null;
            }

            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            return record;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} command(s) left over by a previous run as orphaned")]
    private partial void LogOrphaned(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} ({CommandName}) failed")]
    private partial void LogCommandFailed(long commandId, string commandName, Exception exception);
}
