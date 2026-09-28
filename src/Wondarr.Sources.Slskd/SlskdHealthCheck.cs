using Wondarr.Core.HealthCheck;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Reports the bundled slskd as healthy while it is running, whatever the Soulseek login state is:
/// "running, logged out" is healthy until the user enters credentials (Phase 0 gate).
/// </summary>
public sealed class SlskdHealthCheck : IHealthCheck
{
    /// <summary>Name this check reports its results under.</summary>
    public const string CheckName = "slskd";

    private readonly SlskdStatus _status;
    private readonly IOptionsMonitor<SoulseekOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="SlskdHealthCheck"/> class.</summary>
    public SlskdHealthCheck(SlskdStatus status, IOptionsMonitor<SoulseekOptions> options)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(options);

        _status = status;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var snapshot = _status.Current;

        return Task.FromResult(Evaluate(snapshot, _options.CurrentValue));
    }

    private static HealthCheck Evaluate(SlskdStatusSnapshot snapshot, SoulseekOptions options) =>
        snapshot.State switch
        {
            SlskdState.Disabled => Result(HealthCheckResult.Ok, "External slskd mode (checked in Phase 5)"),
            SlskdState.BinaryMissing => Result(
                HealthCheckResult.Warning,
                snapshot.LastError ?? $"slskd binary not found at {options.BinaryPath}; the Soulseek source is unavailable"),
            SlskdState.NotConfigured => Result(HealthCheckResult.Notice, "slskd has not been started yet"),
            SlskdState.Starting => Result(HealthCheckResult.Notice, "slskd is starting"),
            SlskdState.Restarting => Result(HealthCheckResult.Notice, "slskd is restarting"),
            SlskdState.Stopped => Result(HealthCheckResult.Notice, "slskd is stopped"),
            SlskdState.Crashed => Result(
                HealthCheckResult.Error,
                snapshot.LastError is null ? "slskd is not running" : $"slskd is not running: {snapshot.LastError}"),
            SlskdState.Running when snapshot.IsReachable => Result(
                HealthCheckResult.Ok,
                $"slskd {Version(snapshot)} running; {Soulseek(snapshot, options)}"),
            SlskdState.Running => Result(
                HealthCheckResult.Error,
                snapshot.LastError is null
                    ? "slskd is running but not reachable"
                    : $"slskd is running but not reachable: {snapshot.LastError}"),
            _ => Result(HealthCheckResult.Error, "slskd is in an unknown state"),
        };

    private static string Version(SlskdStatusSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.Version) ? "(version unknown)" : snapshot.Version;

    /// <summary>
    /// "Not configured" and "logged out" look the same to slskd, so the credentials in the settings
    /// decide which one the user is told about.
    /// </summary>
    private static string Soulseek(SlskdStatusSnapshot snapshot, SoulseekOptions options)
    {
        if (!options.HasCredentials)
        {
            return "Soulseek: not configured";
        }

        if (!snapshot.IsLoggedIn)
        {
            return "Soulseek: logged out";
        }

        var username = snapshot.SoulseekUsername ?? options.Username;

        return string.IsNullOrWhiteSpace(username) ? "Soulseek: logged in" : $"Soulseek: logged in as {username}";
    }

    private static HealthCheck Result(HealthCheckResult type, string message) =>
        new(CheckName, type, message, null);
}
