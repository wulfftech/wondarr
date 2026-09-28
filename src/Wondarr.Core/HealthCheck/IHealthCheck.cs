// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/HealthCheck/IProvideHealthCheck.cs, GPL-3.0.
// Adapted for Wondarr: a single async method returning one result; no event or scheduling hooks.

namespace Wondarr.Core.HealthCheck;

/// <summary>One thing Wondarr checks about itself.</summary>
public interface IHealthCheck
{
    /// <summary>The name reported as the result's <c>source</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the check. Implementations report a problem as an <see cref="HealthCheckResult.Error"/>
    /// result; the caller turns a thrown exception into one as well.
    /// </summary>
    Task<HealthCheck> CheckAsync(CancellationToken cancellationToken);
}
