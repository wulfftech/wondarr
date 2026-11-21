using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// After a start, queues <c>UpdateYtDlp</c> when the last successful check is more than a day old or there is none,
/// whatever the scheduler's stored next run says: a container that was down at the scheduled time, or one whose status
/// was lost, must not wait up to a day for its first check. It waits the same five minutes as the scheduler's own first run,
/// so the rest of the host is up. The command itself still skips a run that has nothing to do (auto-update off, a custom
/// path, neither the source nor music videos on), and this does not queue one then either.
/// </summary>
public sealed partial class YtDlpOverdueUpdateCheck : BackgroundService
{
    private readonly IYtDlpBinary _binary;
    private readonly ICommandQueue _commands;
    private readonly TimeProvider _time;
    private readonly ILogger<YtDlpOverdueUpdateCheck> _logger;

    /// <summary>Initialises a new instance of the <see cref="YtDlpOverdueUpdateCheck"/> class.</summary>
    /// <param name="binary">Knows when a check last succeeded.</param>
    /// <param name="commands">Queues the update.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public YtDlpOverdueUpdateCheck(
        IYtDlpBinary binary,
        ICommandQueue commands,
        TimeProvider time,
        ILogger<YtDlpOverdueUpdateCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _binary = binary;
        _commands = commands;
        _time = time;
        _logger = logger;
    }

    /// <summary>Waits the first-run delay, then queues the update if the last successful check is overdue.</summary>
    /// <param name="stoppingToken">Ends the wait.</param>
    /// <returns><see langword="true"/> when the update was queued.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(UpdateYtDlpCommandHandler.FirstRunDelay, _time, stoppingToken).ConfigureAwait(false);

        // Reading the status also loads what the last run kept.
        var status = await _binary.GetStatusAsync(stoppingToken).ConfigureAwait(false);

        if (!status.UpdateActive)
        {
            return false;
        }

        if (status.LastSuccessAt is { } success && _time.GetUtcNow() - success <= UpdateYtDlpCommandHandler.Interval)
        {
            return false;
        }

        // The queue hands back a run that is already queued or running, so the scheduler's own first run is not doubled.
        await _commands.EnqueueAsync(UpdateYtDlpCommandHandler.CommandName, null, CommandTrigger.Scheduled, stoppingToken).ConfigureAwait(false);
        LogQueued(_logger, status.LastSuccessAt);

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
        catch (Exception exception)
        {
            // A failure here must never stop the host: the scheduler's own run is still there.
            LogFailed(_logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The last successful yt-dlp update check was {LastSuccess:o} (or never); queued one")]
    private static partial void LogQueued(ILogger logger, DateTimeOffset? lastSuccess);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The overdue yt-dlp update check could not be queued")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
