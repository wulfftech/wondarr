using Wondarr.Core.Indexers;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Indexers;

/// <summary>
/// The indexers Wondarr asks for release candidates (ARCHITECTURE §5.7). One row per source: which
/// type it is, its settings and how eagerly it is asked. The settings masking and the connection
/// tests live in <see cref="IIndexerService"/> and the types.
/// </summary>
[ApiController]
[Route("api/v1/indexer")]
public sealed class IndexerController : ControllerBase
{
    private readonly IIndexerService _indexers;
    private readonly IReadOnlyList<IIndexerType> _types;

    /// <summary>Initialises a new instance of the <see cref="IndexerController"/> class.</summary>
    /// <param name="indexers">The indexer store.</param>
    /// <param name="types">Every registered indexer type, for the schema and for masking secrets.</param>
    public IndexerController(IIndexerService indexers, IEnumerable<IIndexerType> types)
    {
        ArgumentNullException.ThrowIfNull(indexers);
        ArgumentNullException.ThrowIfNull(types);

        _indexers = indexers;
        _types = [.. types];
    }

    /// <summary>Lists every indexer, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<IndexerResource>>> GetIndexers(CancellationToken cancellationToken)
    {
        var indexers = await _indexers.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(indexers.Select(ToResource).ToList());
    }

    /// <summary>Reads one indexer.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<IndexerResource>> GetIndexer(long id, CancellationToken cancellationToken)
    {
        var indexer = await _indexers.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return indexer is null ? NotFound() : Ok(ToResource(indexer));
    }

    /// <summary>
    /// Describes every indexer type and the settings form it wants, which is what the UI renders the
    /// indexer editor from.
    /// </summary>
    [HttpGet("schema")]
    [Produces("application/json")]
    public ActionResult<List<IndexerSchemaResource>> GetSchema() =>
        Ok(_types
            .Select(type => new IndexerSchemaResource(
                type.Type,
                type.Protocol?.ToString(),
                type.Protocol is null,
                [.. type.Fields.Select(field => new IndexerFieldResource(
                    field.Name,
                    field.Label,
                    field.Type,
                    field.Required,
                    field.HelpText,
                    field.Options,
                    field.Secret,
                    field.Advanced))]))
            .ToList());

    /// <summary>Adds an indexer.</summary>
    /// <param name="resource">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<IndexerResource>> AddIndexer(
        [FromBody] IndexerInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var indexer = await _indexers
                .CreateAsync(IndexerResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetIndexer), new { id = indexer.Id }, ToResource(indexer));
        }
        catch (IndexerValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Replaces an indexer's settings and ask order.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="resource">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<IndexerResource>> UpdateIndexer(
        long id,
        [FromBody] IndexerInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var indexer = await _indexers
                .UpdateAsync(id, IndexerResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return indexer is null ? NotFound() : Ok(ToResource(indexer));
        }
        catch (IndexerValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes an indexer. Followed by <c>200</c> with an empty body, as the other controllers do.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteIndexer(long id, CancellationToken cancellationToken) =>
        await _indexers.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    /// <summary>
    /// Connects with the draft's settings, so the user can check an indexer without waiting for a
    /// search. A body carrying an <c>id</c> keeps that row's stored secrets where the settings carry
    /// the mask back. A type that cannot connect answers <c>200</c> with the failure, never a <c>500</c>.
    /// </summary>
    /// <param name="resource">The indexer to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    [HttpPost("test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ProviderTestResource>> TestIndexer(
        [FromBody] IndexerInputResource resource,
        CancellationToken cancellationToken)
    {
        Core.Sources.ProviderTestResult result;

        try
        {
            result = await _indexers
                .TestAsync(IndexerResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IndexerValidationException exception)
        {
            return Invalid(exception);
        }

        return Ok(new ProviderTestResource(result.Success, result.Error));
    }

    /// <summary>The resource for a row, with every secret masked.</summary>
    private IndexerResource ToResource(Core.Domain.Indexer indexer) =>
        IndexerResourceMapper.ToResource(indexer, TypeFor(indexer.Type));

    /// <summary>The indexer type a name names, or <see langword="null"/> when it is gone.</summary>
    private IIndexerType? TypeFor(string type) =>
        _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Turns the service's validation problem into the RFC 7807 body the API returns.</summary>
    private ActionResult Invalid(IndexerValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }
}
