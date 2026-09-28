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

        try
        {
            await _release.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        return "released";
    }
}
