using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Updates;

namespace Wondarr.Api.Updates;

/// <summary>
/// Whether a newer Wondarr has been released. Wondarr runs in a container and never updates itself:
/// this reports what GitHub says, and the user pulls the new image. The rules live in
/// <see cref="IUpdateCheckService"/>; "run the scheduled check by hand" is also available as the
/// <c>CheckForUpdates</c> command on System &gt; Tasks, and both use the same service and the same limits.
/// </summary>
[ApiController]
[Route("api/v1/update")]
public sealed class UpdateController : ControllerBase
{
    private readonly IUpdateCheckService _updates;
    private readonly IUpdateSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="UpdateController"/> class.</summary>
    /// <param name="updates">Holds the last answer and asks GitHub.</param>
    /// <param name="settings">Reads and writes <c>update.check_enabled</c>.</param>
    public UpdateController(IUpdateCheckService updates, IUpdateSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(settings);

        _updates = updates;
        _settings = settings;
    }

    /// <summary>Reports the running version and the newest release found by the last check. Makes no request.</summary>
    [HttpGet]
    [Produces("application/json")]
    public ActionResult<UpdateResource> GetStatus() => Ok(UpdateResource.From(_updates.GetStatus()));

    /// <summary>
    /// Asks GitHub now. Within a minute of the last successful check it returns that one instead, and
    /// while GitHub's rate limit is in force it makes no request. A failure is the status's
    /// <c>lastError</c>, not an error response.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    [HttpPost("check")]
    [Produces("application/json")]
    public async Task<ActionResult<UpdateResource>> Check(CancellationToken cancellationToken) =>
        Ok(UpdateResource.From(await _updates.CheckAsync(manual: true, cancellationToken).ConfigureAwait(false)));

    /// <summary>Reports whether update checks are on and whether the environment owns the setting.</summary>
    [HttpGet("settings")]
    [Produces("application/json")]
    public ActionResult<UpdateSettingsResource> GetSettings() =>
        Ok(UpdateSettingsResource.From(_settings.Current()));

    /// <summary>
    /// Switches update checks on or off. The file is written and reloaded, so it takes effect without a
    /// restart.
    /// </summary>
    /// <param name="resource">The change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 when the environment owns the setting.</returns>
    [HttpPut("settings")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<UpdateSettingsResource>> UpdateSettings(
        [FromBody] UpdateSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.CheckEnabled, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(UpdateSettingsResource.From(_settings.Current()));
    }
}
