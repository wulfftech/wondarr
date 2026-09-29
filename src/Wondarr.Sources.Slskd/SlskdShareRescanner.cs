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
/// slskd scans its shared folders only when asked (<c>PUT /api/v0/shares</c>) and on its very first
/// start; later starts restore the share cache from its backup ("Share cache loaded from disk"), so a
/// restart does not pick up new files either. Without this, every file imported after the first start
/// would stay unshared — on a fresh install, the whole library. Requests are debounced: a scan
/// runs <see cref="QuietPeriod"/> after the last import, and no later than <see cref="MaxDelay"/>
/// after the first one waiting, so a long run of imports is still shared as it goes. A scan that
/// could not be started (slskd busy scanning, starting, restarting, crashed, or not answering) is
/// retried after <see cref="RetryDelay"/>, and a newer import never brings that retry forward. Only a
/// slskd that is not ours to run (external mode, no binary, host stopping) drops the request.
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

    // Timestamps from _time.GetTimestamp(); _firstPending is null when nothing is waiting, and
    // _notBefore holds a retry back however many imports arrive in the meantime.
    private long? _firstPending;
    private long _dueAt;
    private long _notBefore;

    // Only the first failure in a row is a warning: a slskd that keeps refusing would otherwise log one a minute.
    private bool _failing;

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
            _dueAt = Math.Max(
                Math.Min(now + Ticks(QuietPeriod), _firstPending.Value + Ticks(MaxDelay)),
                _notBefore);

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

            _notBefore = now + Ticks(RetryDelay);
            _firstPending ??= now;
            _dueAt = Math.Max(_dueAt, _notBefore);
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

        var state = _status.Current.State;

        // A start restores the share cache instead of scanning, so the request waits for the process the
        // supervisor is bringing up.
        if (state is SlskdState.Starting or SlskdState.Restarting or SlskdState.Crashed)
        {
            return false;
        }

        // External mode, no binary, or the host stopping: there is no slskd of ours to ask.
        if (state != SlskdState.Running)
        {
            LogSkipped(_logger, state);

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
            _failing = false;

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (_failing)
            {
                LogStillFailing(_logger, exception.Message);
            }
            else
            {
                LogFailed(_logger, exception.Message);
                _failing = true;
            }

            return false;
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asked slskd to rescan its shares after the library changed")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "slskd is already scanning its shares; scanning again in a minute")]
    private static partial void LogBusy(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No share rescan: slskd is {State}, not a process Wondarr runs")]
    private static partial void LogSkipped(ILogger logger, SlskdState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "slskd did not start a share rescan: {Reason}; trying again in a minute")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "slskd still did not start a share rescan: {Reason}")]
    private static partial void LogStillFailing(ILogger logger, string reason);
}
