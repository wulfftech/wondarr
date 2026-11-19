using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// <c>MusicVideoScan</c>: walks the video root, identifies the files it finds against the library's songs and adopts the
/// confident matches where they lie (ADR-0014, the 2026-11-19 amendment). Runs on demand, after the root is set or
/// confirmed, and daily while music videos are on.
/// </summary>
public sealed partial class MusicVideoScanCommandHandler : ICommandHandler
{
    /// <summary>The name the API, the settings hook and the scheduler use.</summary>
    public const string CommandName = "MusicVideoScan";

    /// <summary>How often the scheduled scan runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private const int MaxRootChangeRetries = 3;

    private readonly IMusicVideoFileScanner _scanner;
    private readonly IMusicVideoIdentifier _identifier;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ILogger<MusicVideoScanCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoScanCommandHandler"/> class.</summary>
    /// <param name="scanner">Walks the video root.</param>
    /// <param name="identifier">Identifies what the walk found.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the on/off switch.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoScanCommandHandler(
        IMusicVideoFileScanner scanner,
        IMusicVideoIdentifier identifier,
        IOptionsMonitor<MusicVideoOptions> options,
        ILogger<MusicVideoScanCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scanner = scanner;
        _identifier = identifier;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.CurrentValue.Enabled)
        {
            return "Music videos are turned off.";
        }

        MusicVideoScanResult? scanned = null;

        // A root changed while a scan ran makes that scan's results worthless, and a scan queued meanwhile would only be
        // folded into this running one: so this one simply scans again, a few times at most.
        for (var attempt = 0; attempt < MaxRootChangeRetries && scanned is null; attempt++)
        {
            try
            {
                scanned = await _scanner.ScanAsync(context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (MusicVideoScanRootChangedException)
            {
                // Try again against the new root.
            }
            catch (MusicVideoScanUnavailableException exception)
            {
                LogUnavailable(_logger, exception.Message);

                return $"The video folder was not scanned: {exception.Message}.";
            }
        }

        if (scanned is not { } scan)
        {
            return "The video folder was changed again and again while it was being scanned; nothing was saved. Scan again.";
        }

        // Identification follows the scan: the rows it just wrote are the ones it identifies. The scan already counted;
        // an identification failure only costs this run its identifications, and the next scan tries again.
        var identified = new MusicVideoIdentifyResult(0, 0, 0);

        try
        {
            identified = await _identifier.IdentifyPendingAsync(context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIdentifyFailed(_logger, exception);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{scan.Seen} videos, {scan.Added} added, {scan.Changed} changed, {scan.Unchanged} unchanged, {scan.Missing} missing, {scan.Released} released, {identified.Adopted} adopted, {identified.Ambiguous} ambiguous, {identified.Unmatched} unmatched{(scan.MountProblem ? "; too many videos are missing at once, so none was released" : string.Empty)}");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The video folder was not scanned: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Identifying the videos of the video folder failed; the next scan tries again")]
    private static partial void LogIdentifyFailed(ILogger logger, Exception exception);
}

/// <summary>Queues a scan of the video folder; the settings service calls this after the root is saved or confirmed.</summary>
public interface IMusicVideoScanTrigger
{
    /// <summary>
    /// Queues a <c>MusicVideoScan</c> (or finds the one already queued or running). A queue that refuses never fails the
    /// caller's own work: the daily scan is the fallback.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    Task EnqueueAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoScanTrigger : IMusicVideoScanTrigger
{
    private readonly ICommandQueue _commands;
    private readonly ILogger<MusicVideoScanTrigger> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoScanTrigger"/> class.</summary>
    /// <param name="commands">The command queue.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoScanTrigger(ICommandQueue commands, ILogger<MusicVideoScanTrigger> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(logger);

        _commands = commands;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _commands
                .EnqueueAsync(MusicVideoScanCommandHandler.CommandName, null, CommandTrigger.Manual, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotQueued(_logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scan of the video folder could not be queued; the daily scan will find the videos")]
    private static partial void LogNotQueued(ILogger logger, Exception exception);
}
