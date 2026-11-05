using System.Collections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;

namespace Wondarr.Api.Authentication;

/// <summary>The security setting the UI edits (Settings &gt; General &gt; Security).</summary>
/// <param name="RequireLocalHost">Whether, with <c>auth: none</c>, only a local host name is answered.</param>
/// <param name="RequireLocalHostLocked">Whether the environment sets it (<c>APP__SERVER__REQUIRE_LOCAL_HOST</c>), so it cannot be changed here.</param>
public sealed record SecuritySettingsResource(bool RequireLocalHost, bool RequireLocalHostLocked);

/// <summary>The change to the security setting.</summary>
/// <param name="RequireLocalHost">The new value, or <see langword="null"/> to leave it alone.</param>
public sealed record SecuritySettingsUpdateResource(bool? RequireLocalHost = null);

/// <summary>How an update of the security setting ended.</summary>
/// <param name="Success">Whether the setting was written.</param>
/// <param name="Errors">Why it was refused.</param>
public sealed record SecuritySettingsResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>Reads and writes <c>server.require_local_host</c>, and nothing else in the <c>server</c> section.</summary>
public interface ISecuritySettingsService
{
    /// <summary>Reports the setting and whether the environment owns it.</summary>
    SecuritySettingsResource Current();

    /// <summary>Writes the setting to <c>config.yml</c>; the options reload, so no restart is needed.</summary>
    /// <param name="requireLocalHost">The new value, or <see langword="null"/> to leave it alone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<SecuritySettingsResult> UpdateAsync(bool? requireLocalHost, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="ISecuritySettingsService"/>. A value the environment owns is refused rather than
/// silently overridden, the way the other settings pages do it.
/// </summary>
public sealed class SecuritySettingsService : ISecuritySettingsService
{
    private const string Section = "server";
    private const string Key = "require_local_host";
    private const string EnvironmentVariable = "APP__SERVER__REQUIRE_LOCAL_HOST";

    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly bool _locked;

    /// <summary>Initialises a new instance of the <see cref="SecuritySettingsService"/> class.</summary>
    /// <param name="options">The live options, which already include the environment.</param>
    /// <param name="writer">Writes <c>config.yml</c> and reloads the configuration.</param>
    /// <param name="environment">The process environment, used to find the settings the environment owns.</param>
    public SecuritySettingsService(IOptionsMonitor<ServerOptions> options, IConfigFileWriter writer, IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options;
        _writer = writer;
        _locked = EnvironmentOverrides.SectionKeys(environment, "SERVER").Contains("requirelocalhost");
    }

    /// <inheritdoc />
    public SecuritySettingsResource Current() => new(_options.CurrentValue.RequireLocalHost, _locked);

    /// <inheritdoc />
    public async Task<SecuritySettingsResult> UpdateAsync(bool? requireLocalHost, CancellationToken cancellationToken)
    {
        if (requireLocalHost is null)
        {
            return new SecuritySettingsResult(true, []);
        }

        if (_locked)
        {
            return new SecuritySettingsResult(
                false,
                [$"requireLocalHost is set by the environment variable {EnvironmentVariable} and cannot be changed here"]);
        }

        await _writer
            .UpdateSectionAsync(
                Section,
                new Dictionary<string, object?> { [Key] = requireLocalHost.Value },
                cancellationToken)
            .ConfigureAwait(false);

        return new SecuritySettingsResult(true, []);
    }
}

/// <summary>
/// The switch on Settings &gt; General &gt; Security: whether, with <c>auth: none</c>, Wondarr answers only a
/// local host name (<c>server.require_local_host</c>). Behind the normal API authorisation; the rules live
/// in <see cref="ISecuritySettingsService"/>.
/// </summary>
[ApiController]
[Route("api/v1/config/security")]
public sealed class SecuritySettingsController : ControllerBase
{
    private readonly ISecuritySettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="SecuritySettingsController"/> class.</summary>
    /// <param name="settings">Reads and writes <c>server.require_local_host</c>.</param>
    public SecuritySettingsController(ISecuritySettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>Reports whether only local host names are answered and whether the environment owns the setting.</summary>
    [HttpGet]
    [Produces("application/json")]
    public ActionResult<SecuritySettingsResource> GetSettings() => Ok(_settings.Current());

    /// <summary>
    /// Switches the check on or off. The file is written and reloaded, so the next request sees it without a
    /// restart.
    /// </summary>
    /// <param name="resource">The change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 when the environment owns the setting.</returns>
    [HttpPut]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SecuritySettingsResource>> UpdateSettings(
        [FromBody] SecuritySettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.RequireLocalHost, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(_settings.Current());
    }
}
