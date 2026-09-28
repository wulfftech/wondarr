// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Frontend/InitializeJsonController.cs, GPL-3.0.
// Adapted for Compilarr: only the fields the SPA needs are returned, and the settings come from
// IOptionsMonitor<ServerOptions> rather than a cached config field.

using System.Reflection;
using Compilarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Compilarr.Api.Authentication;

/// <summary>
/// Hands the web UI the API root, the API key and the URL base, as Lidarr's
/// <c>initialize.json</c> does. Guarded by the <c>UI</c> policy, so the key is only served to a
/// caller that already passed the UI's authentication.
/// </summary>
[Authorize(Policy = UiAuthorizationPolicyProvider.PolicyName)]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InitializeController : ControllerBase
{
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="InitializeController"/> class.</summary>
    public InitializeController(IOptionsMonitor<ServerOptions> serverOptions) => _serverOptions = serverOptions;

    /// <summary>Returns the bootstrap payload.</summary>
    [HttpGet("/initialize.json")]
    public IActionResult Index()
    {
        var options = _serverOptions.CurrentValue;

        return Ok(new InitializeResource(
            $"{options.UrlBase}/api/v1",
            options.ApiKey,
            options.UrlBase,
            "Compilarr",
            InformationalVersion));
    }

    private static string InformationalVersion =>
        typeof(InitializeController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}

/// <summary>The <c>initialize.json</c> body. Carries the API key, so never log it.</summary>
/// <param name="ApiRoot">The API root, including the URL base.</param>
/// <param name="ApiKey">The configured API key.</param>
/// <param name="UrlBase">The configured URL base, or an empty string.</param>
/// <param name="InstanceName">The display name of this instance.</param>
/// <param name="Version">The informational version of the running assembly.</param>
public sealed record InitializeResource(
    string ApiRoot,
    string ApiKey,
    string UrlBase,
    string InstanceName,
    string Version);
