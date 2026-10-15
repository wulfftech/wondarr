using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Importing;

namespace Wondarr.Api.Importing;

/// <summary>
/// The import setting on Settings &gt; Download clients: whether an import cleans up what the download
/// left behind (<c>import.clean_leftover_folders</c>). The rules live in <see cref="IImportSettingsService"/>;
/// this controller maps resources and turns a refusal into an RFC 7807 validation problem.
/// </summary>
[ApiController]
[Route("api/v1/config/import")]
public sealed class ImportSettingsController : ControllerBase
{
    private readonly IImportSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="ImportSettingsController"/> class.</summary>
    /// <param name="settings">Reads and writes <c>import.clean_leftover_folders</c>.</param>
    public ImportSettingsController(IImportSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>Reports whether leftover folders are cleaned up and whether the environment owns the setting.</summary>
    [HttpGet]
    [Produces("application/json")]
    public ActionResult<ImportSettingsResource> GetSettings() =>
        Ok(ImportSettingsResource.From(_settings.Current()));

    /// <summary>
    /// Switches the cleanup on or off. The file is written and reloaded, so it takes effect without a
    /// restart.
    /// </summary>
    /// <param name="resource">The change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 when the environment owns the setting.</returns>
    [HttpPut]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ImportSettingsResource>> UpdateSettings(
        [FromBody] ImportSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.CleanLeftoverFolders, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(ImportSettingsResource.From(_settings.Current()));
    }
}
