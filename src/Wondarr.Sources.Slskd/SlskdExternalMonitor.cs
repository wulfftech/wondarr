using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Watches the user's own slskd in external mode: polls <c>GET /api/v0/application</c> and
/// publishes what it said into <see cref="SlskdStatus"/>. Does nothing in bundled mode, where
/// <see cref="SlskdHost"/> owns the status.
/// </summary>
/// <remarks>
/// The mode is re-read on every cycle, so a settings change is picked up without a restart of this
/// service; a change also cancels the wait between polls, so the new settings are polled at once.
/// </remarks>
public sealed partial class SlskdExternalMonitor : BackgroundService
{
    /// <summary>How often the user's slskd is polled.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly SlskdStatus _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlskdExternalMonitor> _logger;

    private CancellationTokenSource? _repoll;

    /// <summary>Initialises a new instance of the <see cref="SlskdExternalMonitor"/> class.</summary>
    public SlskdExternalMonitor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SoulseekOptions> options,
        SlskdStatus status,
        TimeProvider timeProvider,
        ILogger<SlskdExternalMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _status = status;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _repoll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Any options change cancels the wait, so the new settings are polled immediately.
        using var subscription = _options.OnChange(_ => _repoll?.Cancel());

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.CurrentValue.Mode == SoulseekMode.External)
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
            }

            _repoll.Dispose();
            _repoll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            try
            {
                await Task.Delay(PollInterval, _timeProvider, _repoll.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                // An options change: poll again now.
            }
        }
    }

    /// <summary>
    /// Reads the user's slskd's application state and publishes it. Failures are published, never
    /// thrown: the monitor keeps polling, and the health page tells the user what it last saw. The
    /// API key never appears in a message; neither does the full URL — only the host.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var options = _options.CurrentValue;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<ISlskdClient>();
            var state = await client.GetApplicationStateAsync(cancellationToken).ConfigureAwait(false);

            _status.Update(snapshot => snapshot with
            {
                State = SlskdState.External,
                IsReachable = true,
                IsLoggedIn = state.Server.IsLoggedIn,
                Version = string.IsNullOrWhiteSpace(state.Version.Current) ? snapshot.Version : state.Version.Current,
                SoulseekUsername = string.IsNullOrWhiteSpace(state.User.Username) ? null : state.User.Username,
                PendingRestart = state.PendingRestart,
                SharedDirectories = state.Shares.Directories,
                SharedFiles = state.Shares.Files,
                LastError = null,
                LastCheckedAt = now,
            });
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _status.Update(snapshot => snapshot with
            {
                State = SlskdState.External,
                IsReachable = false,
                LastError = "slskd refused the API key",
                LastCheckedAt = now,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The host only — never the full URL (it can carry a path) and never the key.
            var host = ExternalHost(options);

            LogUnreachable(_logger, host);

            _status.Update(snapshot => snapshot with
            {
                State = SlskdState.External,
                IsReachable = false,
                LastError = $"slskd at {host} is not reachable",
                LastCheckedAt = now,
            });
        }
    }

    /// <summary>The host of the user's slskd, or <c>(unknown)</c> when the URL is not set.</summary>
    internal static string ExternalHost(SoulseekOptions options) =>
        Uri.TryCreate(options.External.Url, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host)
            ? url.Host
            : "(unknown)";

    [LoggerMessage(Level = LogLevel.Debug, Message = "slskd at {Host} is not reachable")]
    private static partial void LogUnreachable(ILogger logger, string host);
}