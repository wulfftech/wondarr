using Wondarr.Sources.YouTube;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.YouTube;

/// <summary>
/// The YouTube settings page's backend: what the source is configured with, whether yt-dlp answers,
/// and what a fresh probe says. All the rules live in <see cref="IYouTubeSettingsService"/>; this
/// controller only maps resources and turns a refusal into an RFC 7807 validation problem.
/// </summary>
[ApiController]
[Route("api/v1/youtube")]
public sealed class YouTubeSettingsController : ControllerBase
{
    private readonly IYouTubeSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="YouTubeSettingsController"/> class.</summary>
    /// <param name="settings">Reads and writes the YouTube settings, and runs the health probe.</param>
    public YouTubeSettingsController(IYouTubeSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>Reads the YouTube settings.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    [HttpGet("settings")]
    [Produces("application/json")]
    public async Task<ActionResult<YouTubeSettingsResource>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(YouTubeSettingsResource.From(settings));
    }

    /// <summary>
    /// Changes the YouTube settings. Only the fields present in the body are changed; the file is
    /// written and reloaded, so the source picks the change up without a restart.
    /// </summary>
    /// <param name="resource">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 with the reasons.</returns>
    [HttpPut("settings")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<YouTubeSettingsResource>> UpdateSettings(
        [FromBody] YouTubeSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings
            .UpdateAsync(resource.ToUpdate(), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            // One key for every reason: a validation failure names its own config.yml key, and a
            // read-only field names the environment variable that owns it, so the messages are what
            // the UI shows verbatim.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(YouTubeSettingsResource.From(settings));
    }

    /// <summary>Reports what the health probe last found: the yt-dlp version and the JS-runtime flag.</summary>
    /// <param name="cancellationToken">Cancels the first probe.</param>
    [HttpGet("status")]
    [Produces("application/json")]
    public async Task<ActionResult<YouTubeStatusResource>> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _settings.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return Ok(YouTubeStatusResource.From(status));
    }

    /// <summary>
    /// Runs the health probe now. Always 200: whether yt-dlp answers is the answer, not the status
    /// code. It enables nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    [HttpPost("test")]
    [Produces("application/json")]
    public async Task<ActionResult<YouTubeStatusResource>> Test(CancellationToken cancellationToken)
    {
        var status = await _settings.ProbeAsync(cancellationToken).ConfigureAwait(false);

        return Ok(YouTubeStatusResource.From(status));
    }
}
