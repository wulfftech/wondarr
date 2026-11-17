using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Api.MusicVideos;

/// <summary>
/// The music video settings page's backend (ADR-0014). All the rules live in
/// <see cref="IMusicVideoSettingsService"/>; this controller maps resources and turns a refusal into an RFC 7807
/// validation problem keyed by the field. These endpoints work while the feature is off: that is where it is
/// turned on.
/// </summary>
[ApiController]
[Route("api/v1/musicvideo/settings")]
public sealed class MusicVideoSettingsController : ControllerBase
{
    private readonly IMusicVideoSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoSettingsController"/> class.</summary>
    /// <param name="settings">Reads and writes the music video settings.</param>
    public MusicVideoSettingsController(IMusicVideoSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>Reads the settings and what a live check of the video root finds.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<MusicVideoSettingsResource>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(MusicVideoSettingsResource.From(settings));
    }

    /// <summary>
    /// Changes the settings. Only the fields present in the body are changed; the file is written and reloaded, so
    /// the change applies without a restart. A new root is marked when its folder exists and is empty, or already
    /// holds the marker; otherwise the reply's <c>rootNeedsConfirmation</c> is true.
    /// </summary>
    /// <param name="resource">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 with a field error.</returns>
    [HttpPut]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoSettingsResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MusicVideoSettingsResource>> UpdateSettings(
        [FromBody] MusicVideoSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.ToUpdate(), cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refused(result);
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(MusicVideoSettingsResource.From(settings));
    }

    /// <summary>
    /// Writes the <c>.wondarr-library</c> marker into the saved root, creating the folder when it is not there:
    /// the user has seen that it is missing or unmarked and says it is the right place.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>200 with the settings; 400 (<c>rootPath</c>) when no root is saved, it did not answer, or the marker cannot be written.</returns>
    [HttpPost("root/confirm")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoSettingsResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MusicVideoSettingsResource>> ConfirmRoot(CancellationToken cancellationToken)
    {
        var result = await _settings.ConfirmRootAsync(cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refused(result);
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(MusicVideoSettingsResource.From(settings));
    }

    private ActionResult Refused(MusicVideoSettingsResult result)
    {
        // Each reason under its own field, so the form can show it next to the input; a reason about no single
        // field (an environment lock, a start-up rule) lands under "settings".
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(error.Field, error.Message);
        }

        return ValidationProblem(ModelState);
    }
}
