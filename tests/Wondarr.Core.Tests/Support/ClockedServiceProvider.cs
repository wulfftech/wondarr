using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Testing;

/// <summary>
/// A service provider whose disposal keeps the fake clock moving until it is done. A resilience pipeline
/// waits for the calls it has in flight when it is disposed; a test that failed with one still parked on a
/// clock nobody moves would hang there until the test host is killed, taking every other test with it.
/// </summary>
/// <remarks>
/// This file is compiled into both Wondarr.Core.Tests and Wondarr.Sources.Tests (the second one
/// links it), so each assembly has its own internal copy.
/// </remarks>
internal sealed class ClockedServiceProvider(ServiceProvider inner, TrackingTimeProvider time) : IServiceProvider, IAsyncDisposable
{
    /// <summary>The longest real time the disposal may take; it only matters when a test has already failed.</summary>
    private static readonly TimeSpan DisposeGuard = TimeSpan.FromSeconds(30);

    public object? GetService(Type serviceType) => inner.GetService(serviceType);

    public async ValueTask DisposeAsync()
    {
        var disposing = inner.DisposeAsync().AsTask();
        var guard = Stopwatch.StartNew();

        while (!disposing.IsCompleted && guard.Elapsed < DisposeGuard)
        {
            time.Advance(TimeSpan.FromMinutes(10));

            await Task.Yield();
        }

        if (!disposing.IsCompleted)
        {
            throw new TimeoutException("The service provider was still being disposed after the fake clock was run on.");
        }

        await disposing;
    }
}
