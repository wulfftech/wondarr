using Wondarr.Core.Jobs;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// A handler that starts, blocks until the test releases it, and records how many copies of itself
/// ran at the same time. The running count is how the tests prove the executor's concurrency limit
/// and the "never the same name twice" rule.
/// </summary>
internal sealed class BlockingCommandHandler : ICommandHandler, IDisposable
{
    private readonly SemaphoreSlim _release = new(0);
    private readonly Lock _peakLock = new();

    private int _started;
    private int _running;

    public BlockingCommandHandler(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
    }

    public string Name { get; }

    /// <inheritdoc />
    public bool CancelWhileRunning { get; init; }

    /// <summary>Gets or sets what runs inline when the user's cancel fires, as a token callback does.</summary>
    public Action? OnUserCancel { get; set; }

    /// <summary>Gets a value indicating whether the handler's token was cancelled while it waited.</summary>
    public bool SawCancellation { get; private set; }

    /// <summary>How many times <see cref="ExecuteAsync"/> was entered.</summary>
    public int StartedCount => Volatile.Read(ref _started);

    /// <summary>The most copies that were inside <see cref="ExecuteAsync"/> at the same time.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>Lets every blocked call return.</summary>
    public void Release() => _release.Release(100);

    /// <inheritdoc />
    public void Dispose()
    {
        _release.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref _started);
        Interlocked.Increment(ref _running);

        lock (_peakLock)
        {
            if (running > PeakConcurrency)
            {
                PeakConcurrency = running;
            }
        }

        // The user's cancel arrives as its own token; only the stopping token comes in as `cancellationToken`.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.UserCancellation);
        using var callback = context.UserCancellation.Register(() => OnUserCancel?.Invoke());

        try
        {
            await _release.WaitAsync(waiting.Token);
        }
        catch (OperationCanceledException)
        {
            SawCancellation = true;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        return "released";
    }
}
