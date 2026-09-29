using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Asks the bundled slskd to rescan its shares when the library has changed.
/// </summary>
/// <remarks>
/// slskd scans its shared folders when it starts and otherwise only when asked
/// (<c>PUT /api/v0/shares</c>), so without this every file imported after start would stay unshared
/// until the next restart — on a fresh install, the whole library. Requests are debounced: a scan
/// runs <see cref="QuietPeriod"/> after the last import, and no later than <see cref="MaxDelay"/>
/// after the first one waiting, so a long run of imports is still shared as it goes. A scan that
/// could not be started (slskd busy scanning, or not answering) is retried after
/// <see cref="RetryDelay"/>; a slskd that is not running needs none, because it scans when it starts.
/// </remarks>
public sealed partial class SlskdShareRescanner : BackgroundService, IHandle<SongImportedEvent>
{
    /// <summary>How long the library has to be quiet before a scan starts.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>The longest a request waits for the library to go quiet.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>The wait before a scan that could not be started is tried again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly SlskdStatus _status;
    private readonly TimeProvider _time;
    private readonly ILogger<SlskdShareRescanner> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    // Timestamps from _time.GetTimestamp(); _firstPending is null when nothing is waiting.
    private long? _firstPending;
    private long _dueAt;

    /// <summary>Initialises a new instance of the <see cref="SlskdShareRescanner"/> class.</summary>
    public SlskdShareRescanner(
        IServiceScopeFactory scopes,
        IOptionsMonitor<SoulseekOptions> options,
        SlskdStatus status,
        TimeProvider time,
        ILogger<SlskdShareRescanner> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _status = status;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether a scan is waiting to run.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _firstPending is not null;
            }
        }
    }

    /// <inheritdoc />
    public Task HandleAsync(SongImportedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        RequestRescan();

        return Task.CompletedTask;
    }

    /// <summary>Asks for a scan once the library has been quiet for <see cref="QuietPeriod"/>.</summary>
    public void RequestRescan()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();

            _firstPending ??= now;
            _dueAt = Math.Min(now + Ticks(QuietPeriod), _firstPending.Value + Ticks(MaxDelay));

            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A settings save can change what is shared without restarting slskd — two quick saves that end
        // where they began, say, leave the running process with a reloaded share list it has not scanned.
        // A redundant scan is cheap, so any reload with sharing on asks for one.
        using var subscription = _options.OnChange(options =>
        {
            if (options.ShareLibrary)
            {
                RequestRescan();
            }
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);

                while (NextWait() is { } wait)
                {
                    if (wait > TimeSpan.Zero)
                    {
                        // Re-evaluated afterwards: a newer request may have moved the due time.
                        await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    lock (_gate)
                    {
                        _firstPending = null;
                    }

                    if (!await TryRescanAsync(stoppingToken).ConfigureAwait(false))
                    {
                        Retry();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; a pending scan is not worth delaying that for.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    /// <summary>The wait until the pending scan is due, or <see langword="null"/> when none is pending.</summary>
    private TimeSpan? NextWait()
    {
        lock (_gate)
        {
            if (_firstPending is null)
            {
                return null;
            }

            var remaining = _dueAt - _time.GetTimestamp();

            if (remaining <= 0)
            {
                return TimeSpan.Zero;
            }

            // Rounded up to whole milliseconds: Task.Delay truncates, and a sub-millisecond wait would spin.
            return TimeSpan.FromMilliseconds(Math.Ceiling(remaining * 1000.0 / _time.TimestampFrequency));
        }
    }

    /// <summary>Keeps the scan pending, due no earlier than <see cref="RetryDelay"/> from now.</summary>
    private void Retry()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();

            _firstPending ??= now;
            _dueAt = Math.Max(_dueAt, now + Ticks(RetryDelay));
        }
    }

    /// <summary>
    /// Starts a scan. Returns <see langword="false"/> when it should be tried again; a request with
    /// nothing to do (sharing off, slskd not running) is dropped.
    /// </summary>
    private async Task<bool> TryRescanAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.ShareLibrary || options.SharedFolders.Count == 0)
        {
            return true;
        }

        // Not running means external mode, no binary, or a process that is starting, restarting or about
        // to be restarted — and a slskd of ours that starts scans its shares by itself.
        if (_status.Current.State != SlskdState.Running)
        {
            LogSkipped(_logger, _status.Current.State);

            return true;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ISlskdClient>();

            var outcome = await client.RescanSharesAsync(cancellationToken).ConfigureAwait(false);

            if (outcome == SlskdRescanOutcome.AlreadyScanning)
            {
                // The running scan may have passed the new files already; one more afterwards makes sure.
                LogBusy(_logger);

                return false;
            }

            LogStarted(_logger);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, exception.Message);

            return false;
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asked slskd to rescan its shares after the library changed")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "slskd is already scanning its shares; scanning again in a minute")]
    private static partial void LogBusy(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No share rescan: slskd is {State}, and scans its shares when it starts")]
    private static partial void LogSkipped(ILogger logger, SlskdState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "slskd did not start a share rescan: {Reason}; trying again in a minute")]
    private static partial void LogFailed(ILogger logger, string reason);
}
