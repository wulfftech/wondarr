using Wondarr.Core.Jobs;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// A handler that hands its executor worker back, waits for the test to let it go, takes a worker
/// again and finishes. Counts how many handlers hold a worker at the same time.
/// </summary>
internal sealed class YieldingCommandHandler : ICommandHandler, IDisposable
{
    private static int _working;

    private readonly SemaphoreSlim _release = new(0);
    private int _yielded;

    public YieldingCommandHandler(string name) => Name = name;

    public string Name { get; }

    /// <summary>The most yielding handlers (of any name) that held a worker at the same time.</summary>
    public static int PeakWorking { get; private set; }

    /// <summary>Whether this command has handed its worker back and is waiting.</summary>
    public bool IsWaiting => Volatile.Read(ref _yielded) == 1;

    /// <summary>Lets the waiting call finish.</summary>
    public void Release() => _release.Release();

    /// <summary>Clears the shared counters.</summary>
    public static void Reset()
    {
        Volatile.Write(ref _working, 0);
        PeakWorking = 0;
    }

    public void Dispose() => _release.Dispose();

    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Enter();

        var lease = context.YieldWorker?.Invoke();
        Leave();
        Volatile.Write(ref _yielded, lease is null ? 2 : 1);

        try
        {
            await _release.WaitAsync(cancellationToken);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            Enter();
        }

        Leave();

        return "done";
    }

    private static void Enter()
    {
        var now = Interlocked.Increment(ref _working);

        lock (typeof(YieldingCommandHandler))
        {
            if (now > PeakWorking)
            {
                PeakWorking = now;
            }
        }
    }

    private static void Leave() => Interlocked.Decrement(ref _working);
}
