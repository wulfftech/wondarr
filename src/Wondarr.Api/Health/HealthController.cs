// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/Health/HealthController.cs, GPL-3.0.
// Adapted for Wondarr: no SignalR broadcast, and every result is returned rather than only the problems.

using Wondarr.Core.HealthCheck;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Health;

/// <summary>The health endpoint the UI and the Phase 0 gate read.</summary>
[ApiController]
[Route("api/v1/health")]
public sealed class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;

    /// <summary>Initialises a new instance of the <see cref="HealthController"/> class.</summary>
    public HealthController(HealthCheckService healthCheckService)
    {
        ArgumentNullException.ThrowIfNull(healthCheckService);

        _healthCheckService = healthCheckService;
    }

    /// <summary>
    /// Returns every health check result, <c>ok</c> ones included. This is a deliberate difference
    /// from Lidarr, which lists only the problems: the Phase 0 gate has to show the database and the
    /// folders as healthy, not just the absence of errors.
    /// </summary>
    /// <param name="refresh">Forces the checks to run instead of reusing the 60-second cache.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>200 when nothing is broken, 503 when at least one check reported an error.</returns>
    [HttpGet]
    [Produces("application/json")]
    public async Task<IActionResult> GetHealth(
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        var results = await _healthCheckService.GetResultsAsync(refresh, cancellationToken).ConfigureAwait(false);

        var resources = results
            .Select(result => new HealthResource(result.Source, result.Type, result.Message, result.WikiUrl?.ToString()))
            .ToList();

        return results.Any(result => result.Type == HealthCheckResult.Error)
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, resources)
            : Ok(resources);
    }
}
