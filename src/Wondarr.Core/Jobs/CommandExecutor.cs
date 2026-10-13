using System.Threading.Channels;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Runs queued commands. The queue hands out ids over a channel and this service drains it, so no
/// polling is needed. At most <see cref="MaxConcurrency"/> commands work at once; a command that only
/// waits (for a download slot) may hand its place back with <see cref="CommandContext.YieldWorker"/>,
/// so a few waiting commands never starve the rest.
/// </summary>
public sealed partial class CommandExecutor : BackgroundService
{
    /// <summary>How many commands may run at the same time.</summary>
    public const int MaxConcurrency = 3;

    /// <summary>How many commands may be waiting with their place handed back at the same time.</summary>
    public const int MaxYielded = 16;

    /// <summary>How long a worker waits after a command's bookkeeping threw, before it takes the next one.</summary>
    internal static readonly TimeSpan WorkerFaultPause = TimeSpan.FromSeconds(1);

    /// <summary>How often a status write is tried before the executor gives up on it.</summary>
    internal const int StatusWriteAttempts = 3;

    /// <summary>How long the executor waits between two tries of a status write.</summary>
    internal static readonly TimeSpan StatusWriteRetryPause = TimeSpan.FromMilliseconds(250);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventAggregator _eventAggregator;
    private readonly ChannelReader<long> _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ActiveCommands _activeCommands;
    private readonly ILogger<CommandExecutor> _logger;

    // Every status write is serialised: SQLite has one writer at a time, and three workers racing
    // for it would turn into "database is locked" under load.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // The places commands work in: a command holds one while it works and may hand it back while it
    // only waits. The loops that read the channel outnumber the places by MaxYielded, so a command
    // that handed its place back has a spare loop to take the next command.
    private readonly SemaphoreSlim _places = new(MaxConcurrency, MaxConcurrency);
    private int _yielded;

    /// <summary>Initialises a new instance of the <see cref="CommandExecutor"/> class.</summary>
    public CommandExecutor(
        IServiceScopeFactory scopeFactory,
        IEventAggregator eventAggregator,
        Channel<long> queue,
        TimeProvider timeProvider,
        ActiveCommands activeCommands,
        ILogger<CommandExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(activeCommands);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _eventAggregator = eventAggregator;
        _queue = queue.Reader;
        _timeProvider = timeProvider;
        _activeCommands = activeCommands;
        _logger = logger;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _writeGate.Dispose();
        _places.Dispose();
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
        var workers = new Task[MaxConcurrency + MaxYielded];
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
                // A place is taken only once there is a command to run. An idle loop holding one while
                // it waits for work would keep a command that handed its place back from taking it again.
                await _places.WaitAsync(stoppingToken).ConfigureAwait(false);
                var place = new WorkerPlace(this);

                try
                {
                    await RunAsync(id, place, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // A failed status write (SQLITE_BUSY, say) must not fault the worker and stop the
                    // host. The pause keeps a persistent fault from turning into a tight loop.
                    LogWorkerFault(id, exception);
                    await FailStrandedAsync(id, exception.Message).ConfigureAwait(false);
                    await Task.Delay(WorkerFaultPause, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    place.Release();
                    _activeCommands.Remove(id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: anything in flight has already been marked aborted by RunAsync.
        }
    }

    private async Task RunAsync(long id, WorkerPlace place, CancellationToken stoppingToken)
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
        CommandHandlerLookup? lookup = null;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            lookup = CommandHandlerResolver.Find(scope.ServiceProvider, started.Name);
            var handler = lookup.Handler;

            if (handler is null && lookup.Failures.Count > 0)
            {
                // A handler DI cannot build fails this command once, with the reason; it must not
                // leave the row queued or take the other commands down with it.
                error = $"Command '{started.Name}' could not run: a handler could not be built ({string.Join("; ", lookup.Failures)}).";
                message = error;
                LogHandlerNotBuilt(started.Id, started.Name, error);
            }
            else if (handler is null)
            {
                error = $"No handler is registered for command '{started.Name}'.";
            }
            else
            {
                var context = new CommandContext(
                    started.Id,
                    started.Body,
                    started.Trigger,
                    progress => ReportProgressAsync(started.Id, progress),
                    place.Yield);

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
        finally
        {
            // Only a handler built outside the scope's own tracking needs this.
            if (lookup is not null)
            {
                await lookup.ReleaseAsync().ConfigureAwait(false);
            }
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
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

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

        // One conditional update: a cancel that lands between a read and a write would otherwise be
        // overwritten, and the command would run with a row that says Cancelled.
        return await RetryAsync(async () =>
        {
            await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

                var changed = await context.Commands
                    .Where(command => command.Id == id && command.Status == CommandStatus.Queued)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(command => command.Status, CommandStatus.Started)
                            .SetProperty(command => command.StartedAt, (DateTime?)now)
                            .SetProperty(command => command.Result, CommandResult.Unknown)
                            .SetProperty(command => command.UpdatedAt, now),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (changed == 0)
                {
                    // Cancelled while it waited, or another worker got there first.
                    return null;
                }

                return await context.Commands
                    .AsNoTracking()
                    .FirstOrDefaultAsync(command => command.Id == id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails a command whose bookkeeping broke, so it is not left Queued or Started for dedupe to find.
    /// Best effort: when the database is still unwritable the row stays, and the queue fails it as
    /// stranded at the next enqueue.
    /// </summary>
    private async Task FailStrandedAsync(long id, string reason)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var failed = await UpdateAsync(
                id,
                record =>
                {
                    if (record.Status is not (CommandStatus.Queued or CommandStatus.Started))
                    {
                        return false;
                    }

                    record.Status = CommandStatus.Failed;
                    record.Result = CommandResult.Unsuccessful;
                    record.Exception = $"The executor could not record this command's progress: {reason}";
                    record.EndedAt = now;

                    return true;
                }).ConfigureAwait(false);

            if (failed is not null)
            {
                await _eventAggregator
                    .PublishAsync(new CommandUpdatedEvent(failed), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            LogStrandedNotFailed(id, exception);
        }
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
    private Task<CommandRecord?> UpdateAsync(long id, Func<CommandRecord, bool> mutate) =>
        RetryAsync(async () =>
        {
            await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

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
        });

    /// <summary>
    /// Runs a status write, trying again a few times when it throws. SQLite's own busy timeout waits for
    /// a lock; this covers the write that still fails (a lock held longer than that, a transient fault),
    /// which would otherwise strand the command.
    /// </summary>
    private async Task<CommandRecord?> RetryAsync(Func<Task<CommandRecord?>> write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < StatusWriteAttempts && exception is not OperationCanceledException)
            {
                LogStatusWriteRetry(attempt, exception);
                await Task.Delay(StatusWriteRetryPause, _timeProvider, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One of the places a command works in, held for as long as the command is running.</summary>
    private sealed class WorkerPlace(CommandExecutor executor)
    {
        private readonly object _lock = new();
        private bool _held = true;
        private bool _leased;

        /// <summary>Hands the place back for the rest of the command, or reports that it cannot.</summary>
        public IAsyncDisposable? Yield()
        {
            lock (_lock)
            {
                if (!_held || Interlocked.Increment(ref executor._yielded) > MaxYielded)
                {
                    if (_held)
                    {
                        Interlocked.Decrement(ref executor._yielded);
                    }

                    return null;
                }

                _held = false;
                _leased = true;
            }

            executor._places.Release();

            return new Lease(this);
        }

        /// <summary>Gives the place back when the command is done with it.</summary>
        public void Release()
        {
            lock (_lock)
            {
                if (!_held)
                {
                    if (_leased)
                    {
                        // The command ended without taking its place back.
                        _leased = false;
                        Interlocked.Decrement(ref executor._yielded);
                    }

                    return;
                }

                _held = false;
            }

            executor._places.Release();
        }

        private async ValueTask TakeBackAsync()
        {
            // Not cancellable: the command goes on to finish its own bookkeeping either way, and the
            // places are freed by commands that honour the shutdown token.
            await executor._places.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            lock (_lock)
            {
                _held = true;
                _leased = false;
            }

            Interlocked.Decrement(ref executor._yielded);
        }

        private sealed class Lease(WorkerPlace place) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync() =>
                Interlocked.Exchange(ref _disposed, 1) == 0 ? place.TakeBackAsync() : ValueTask.CompletedTask;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} command(s) left over by a previous run as orphaned")]
    private partial void LogOrphaned(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The executor could not record the outcome of command {CommandId}")]
    private partial void LogWorkerFault(long commandId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A command status write failed (attempt {Attempt}); trying again")]
    private partial void LogStatusWriteRetry(int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} could not be marked failed after its bookkeeping broke; the next enqueue will fail it as stranded")]
    private partial void LogStrandedNotFailed(long commandId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} ({CommandName}) failed: {Reason}")]
    private partial void LogHandlerNotBuilt(long commandId, string commandName, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} ({CommandName}) failed")]
    private partial void LogCommandFailed(long commandId, string commandName, Exception exception);
}
