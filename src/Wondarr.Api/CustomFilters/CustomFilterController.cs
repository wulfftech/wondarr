using System.Text.Json;
using Wondarr.Core.CustomFilters;
using Wondarr.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.CustomFilters;

/// <summary>Saved views of the list pages (Lidarr's custom filters): a label and the filters the UI applies.</summary>
[ApiController]
[Route("api/v1/customfilter")]
public sealed class CustomFilterController : ControllerBase
{
    private readonly ICustomFilterService _filters;

    /// <summary>Initialises a new instance of the <see cref="CustomFilterController"/> class.</summary>
    /// <param name="filters">The saved-view store.</param>
    public CustomFilterController(ICustomFilterService filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        _filters = filters;
    }

    /// <summary>Lists the saved views, optionally of one type.</summary>
    /// <param name="type">Only views of this type, for example <c>library</c>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<CustomFilterResource>>> GetCustomFilters(
        string? type,
        CancellationToken cancellationToken)
    {
        var rows = await _filters.ListAsync(type, cancellationToken).ConfigureAwait(false);

        return Ok(rows.Select(ToResource).ToList());
    }

    /// <summary>Reads one saved view.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<CustomFilterResource>> GetCustomFilter(long id, CancellationToken cancellationToken)
    {
        var row = await _filters.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return row is null ? NotFound() : Ok(ToResource(row));
    }

    /// <summary>Saves a view.</summary>
    /// <param name="resource">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<CustomFilterResource>> AddCustomFilter(
        [FromBody] CustomFilterInputResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var row = await _filters
                .CreateAsync(new CustomFilterDraft(resource.Type, resource.Label, resource.Filters), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetCustomFilter), new { id = row.Id }, ToResource(row));
        }
        catch (CustomFilterValidationException exception)
        {
            return Invalid(exception);
        }
        catch (CustomFilterConflictException exception)
        {
            return Conflicting(exception);
        }
    }

    /// <summary>Replaces a saved view.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="resource">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<CustomFilterResource>> UpdateCustomFilter(
        long id,
        [FromBody] CustomFilterInputResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var row = await _filters
                .UpdateAsync(id, new CustomFilterDraft(resource.Type, resource.Label, resource.Filters), cancellationToken)
                .ConfigureAwait(false);

            return row is null ? NotFound() : Ok(ToResource(row));
        }
        catch (CustomFilterValidationException exception)
        {
            return Invalid(exception);
        }
        catch (CustomFilterConflictException exception)
        {
            return Conflicting(exception);
        }
    }

    /// <summary>Deletes a saved view.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteCustomFilter(long id, CancellationToken cancellationToken) =>
        await _filters.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    private static CustomFilterResource ToResource(CustomFilter row)
    {
        using var document = JsonDocument.Parse(row.Filters);

        return new CustomFilterResource(row.Id, row.Type, row.Label, document.RootElement.Clone());
    }

    private ActionResult Invalid(CustomFilterValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }

    private ObjectResult Conflicting(CustomFilterConflictException exception) =>
        Problem(
            title: "Saved view already exists",
            detail: exception.Message,
            statusCode: StatusCodes.Status409Conflict);
}

/// <summary>A saved view.</summary>
/// <param name="Id">The view id.</param>
/// <param name="Type">The page it belongs to, for example <c>library</c>.</param>
/// <param name="Label">Its name; unique within the type.</param>
/// <param name="Filters">The filters: an array of <c>{ key, value, type }</c> objects.</param>
public sealed record CustomFilterResource(long Id, string Type, string Label, JsonElement Filters);

/// <summary>The body of a create or an update.</summary>
/// <param name="Type">The page it belongs to.</param>
/// <param name="Label">Its name, 1 to 100 characters.</param>
/// <param name="Filters">A JSON array of at most 20 objects, each with a string <c>key</c>, at most 8 KB.</param>
public sealed record CustomFilterInputResource(string? Type, string? Label, JsonElement Filters);
