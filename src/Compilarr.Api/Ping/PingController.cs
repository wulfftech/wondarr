// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Ping/PingController.cs, GPL-3.0.
// Adapted for Compilarr: always reports OK; there is no config cache to probe yet.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Compilarr.Api.Ping;

/// <summary>The liveness probe used by the container images and *arr-style clients.</summary>
[AllowAnonymous]
[ApiController]
public sealed class PingController : ControllerBase
{
    /// <summary>Reports that the process is up. Never requires credentials.</summary>
    [HttpGet("/ping")]
    [HttpHead("/ping")]
    [Produces("application/json")]
    public IActionResult GetStatus() => Ok(new PingResource("OK"));
}

/// <summary>The <c>/ping</c> response body.</summary>
/// <param name="Status">Always <c>OK</c>.</param>
public sealed record PingResource(string Status);
