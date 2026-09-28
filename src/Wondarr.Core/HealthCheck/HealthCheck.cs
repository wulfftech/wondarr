// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/HealthCheck/HealthCheck.cs, GPL-3.0.
// Adapted for Wondarr: drops ModelBase and HealthCheckReason, Source is the check's name rather
// than a Type, and WikiUrl is left null until there is a wiki to point at.

namespace Wondarr.Core.HealthCheck;

/// <summary>How serious a health check result is, ordered from fine to broken.</summary>
public enum HealthCheckResult
{
    /// <summary>Everything is as it should be.</summary>
    Ok = 0,

    /// <summary>Worth telling the user about, but nothing is broken.</summary>
    Notice = 1,

    /// <summary>The app runs, but something is misconfigured.</summary>
    Warning = 2,

    /// <summary>The app cannot do its job until this is fixed.</summary>
    Error = 3,
}

/// <summary>The outcome of one health check.</summary>
/// <param name="Source">The name of the check that produced this result.</param>
/// <param name="Type">How serious the result is.</param>
/// <param name="Message">A sentence for the user. Never contains a secret.</param>
/// <param name="WikiUrl">The wiki page explaining this result, or <see langword="null"/>.</param>
public sealed record HealthCheck(string Source, HealthCheckResult Type, string Message, Uri? WikiUrl);
