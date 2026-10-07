using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Backup;

/// <summary>
/// Stops the application a moment after a response is written, so the supervisor (s6 in the image)
/// starts it again — which is how a staged restore is applied. Behind an interface so a test host
/// can stub the shutdown out instead of stopping itself.
/// </summary>
public interface IApplicationShutdown
{
    /// <summary>
    /// Stops the application shortly after the current response has been written.
    /// </summary>
    void StopAfterResponse();
}

/// <summary>
/// The real shutdown: <see cref="IHostApplicationLifetime.StopApplication"/> scheduled a second out,
/// so the response that asked for the restart reaches the client first.
/// </summary>
public sealed partial class HostApplicationShutdown : IApplicationShutdown
{
    /// <summary>How long the host keeps serving after a shutdown is requested.</summary>
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(1);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostApplicationShutdown> _logger;

    /// <summary>Initialises a new instance of the <see cref="HostApplicationShutdown"/> class.</summary>
    /// <param name="lifetime">The host's lifetime, whose stop makes the process exit.</param>
    /// <param name="timeProvider">The clock the shutdown delay waits on.</param>
    /// <param name="logger">The logger.</param>
    public HostApplicationShutdown(
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<HostApplicationShutdown> logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void StopAfterResponse()
    {
        _ = Task.Delay(ShutdownDelay, _timeProvider)
            .ContinueWith(
                _ => Stop(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void Stop()
    {
        LogStopping(_logger);
        _lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping so a staged restore can be applied on the next start")]
    private static partial void LogStopping(ILogger logger);
}
