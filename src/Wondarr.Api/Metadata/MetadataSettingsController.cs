using Wondarr.Core.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Metadata;

/// <summary>
/// The Metadata services card of Settings &gt; General: whether the optional AcoustID and Last.fm keys
/// are set, a way to change them, and a Test button for each. All the rules live in
/// <see cref="IMetadataSettingsService"/>; this controller maps resources and turns a refusal into an
/// RFC 7807 validation problem. No response ever carries a key.
/// </summary>
[ApiController]
[Route("api/v1/metadata")]
public sealed class MetadataSettingsController : ControllerBase
{
    private readonly IMetadataSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="MetadataSettingsController"/> class.</summary>
    /// <param name="settings">Reads and writes the keys, and tests them.</param>
    public MetadataSettingsController(IMetadataSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>Reports which keys are set and which the environment owns. Never returns a key.</summary>
    [HttpGet("settings")]
    [Produces("application/json")]
    public ActionResult<MetadataSettingsResource> GetSettings() =>
        Ok(MetadataSettingsResource.From(_settings.Current()));

    /// <summary>
    /// Changes the keys. An absent field is left alone and an empty string removes the key; the file is
    /// written and reloaded, so a key takes effect without a restart.
    /// </summary>
    /// <param name="resource">The keys to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 with the reasons.</returns>
    [HttpPut("settings")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<MetadataSettingsResource>> UpdateSettings(
        [FromBody] MetadataSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.ToUpdate(), cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            // A read-only field names the environment variable that owns it, exactly as the other
            // settings pages do, so the message is what the UI shows verbatim.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(MetadataSettingsResource.From(_settings.Current()));
    }

    /// <summary>
    /// Makes one call to Last.fm (<c>track.getInfo</c>) or AcoustID (a lookup that only checks the key
    /// is accepted) with the typed key, or the stored one when none is sent. Always 200 for a known
    /// service: whether the key works is the answer, not the status code.
    /// </summary>
    /// <param name="request">Which service, and optionally the key to try.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [HttpPost("settings/test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<MetadataKeyTestResource>> Test(
        [FromBody] MetadataKeyTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        MetadataKeyService service;

        if (string.Equals(request.Service, "lastfm", StringComparison.OrdinalIgnoreCase))
        {
            service = MetadataKeyService.LastFm;
        }
        else if (string.Equals(request.Service, "acoustid", StringComparison.OrdinalIgnoreCase))
        {
            service = MetadataKeyService.AcoustId;
        }
        else
        {
            ModelState.AddModelError("service", "service must be lastfm or acoustid");

            return ValidationProblem(ModelState);
        }

        var result = await _settings.TestAsync(service, request.Key, cancellationToken).ConfigureAwait(false);

        return Ok(new MetadataKeyTestResource(result.Ok, result.Message));
    }
}
