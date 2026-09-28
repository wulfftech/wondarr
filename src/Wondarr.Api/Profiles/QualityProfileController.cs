using Wondarr.Core.Domain;
using Wondarr.Core.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Profiles;

/// <summary>
/// The quality profiles the Settings UI and *arr clients edit. All the rules live in
/// <see cref="IQualityProfileService"/>; this controller only maps resources to entities, turns a
/// validation failure into an RFC 7807 validation problem and a profile that is still in use into a
/// 409.
/// </summary>
[ApiController]
[Route("api/v1/qualityprofile")]
public sealed class QualityProfileController : ControllerBase
{
    private readonly IQualityProfileService _profiles;
    private readonly IQualityDefinitionService _qualities;

    /// <summary>Initialises a new instance of the <see cref="QualityProfileController"/> class.</summary>
    /// <param name="profiles">The quality profile service.</param>
    /// <param name="qualities">The quality ladder, for the display names in an item group.</param>
    public QualityProfileController(IQualityProfileService profiles, IQualityDefinitionService qualities)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(qualities);

        _profiles = profiles;
        _qualities = qualities;
    }

    /// <summary>Lists every quality profile, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<QualityProfileResource>>> GetProfiles(CancellationToken cancellationToken)
    {
        var profiles = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var qualities = await QualityNamesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(profiles.Select(profile => profile.ToResource(qualities)).ToList());
    }

    /// <summary>Reads one quality profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<QualityProfileResource>> GetProfile(long id, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return NotFound();
        }

        var qualities = await QualityNamesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(profile.ToResource(qualities));
    }

    /// <summary>Creates a quality profile.</summary>
    /// <param name="resource">The profile to create; its <c>id</c> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>201 with the stored profile and its <c>Location</c>.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<QualityProfileResource>> AddProfile(
        [FromBody] QualityProfileResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _profiles.AddAsync(resource.ToProfile(0), cancellationToken).ConfigureAwait(false);
            var qualities = await QualityNamesAsync(cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(
                nameof(GetProfile),
                new { id = created.Id },
                created.ToResource(qualities));
        }
        catch (ProfileValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Replaces a quality profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="resource">The new values; the body's <c>id</c> must match the route or be 0.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<QualityProfileResource>> UpdateProfile(
        long id,
        [FromBody] QualityProfileResource resource,
        CancellationToken cancellationToken)
    {
        if (resource.Id != 0 && resource.Id != id)
        {
            ModelState.AddModelError("id", $"The body id {resource.Id} does not match the route id {id}.");

            return ValidationProblem(ModelState);
        }

        if (await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound();
        }

        try
        {
            var updated = await _profiles.UpdateAsync(resource.ToProfile(id), cancellationToken).ConfigureAwait(false);
            var qualities = await QualityNamesAsync(cancellationToken).ConfigureAwait(false);

            return Ok(updated.ToResource(qualities));
        }
        catch (ProfileValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes a quality profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 when it was deleted, 404 when the id is unknown, 409 when a song still uses it.</returns>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteProfile(long id, CancellationToken cancellationToken)
    {
        try
        {
            return await _profiles.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                ? Ok()
                : NotFound();
        }
        catch (ProfileInUseException exception)
        {
            return Problem(
                title: "Quality profile in use",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Turns the service's problems into an RFC 7807 validation problem.</summary>
    private ActionResult Invalid(ProfileValidationException exception)
    {
        foreach (var (property, message) in exception.Errors)
        {
            ModelState.AddModelError(property, message);
        }

        return ValidationProblem(ModelState);
    }

    /// <summary>Reads the ladder once so every item group can carry its qualities' display names.</summary>
    private async Task<IReadOnlyDictionary<long, Quality>> QualityNamesAsync(CancellationToken cancellationToken)
    {
        var qualities = await _qualities.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return qualities.ToDictionary(quality => quality.Id);
    }
}
