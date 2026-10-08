using Wondarr.Api.Indexers;
using Wondarr.Core.DownloadClients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.DownloadClients;

/// <summary>
/// The download clients Wondarr sends grabs to (ARCHITECTURE §5.7). One row per client: which type
/// it is and its settings. The settings masking and the connection tests live in
/// <see cref="IDownloadClientService"/> and the types.
/// </summary>
[ApiController]
[Route("api/v1/downloadclient")]
public sealed class DownloadClientController : ControllerBase
{
    private readonly IDownloadClientService _clients;
    private readonly IReadOnlyList<IDownloadClientType> _types;

    /// <summary>Initialises a new instance of the <see cref="DownloadClientController"/> class.</summary>
    /// <param name="clients">The download client store.</param>
    /// <param name="types">Every registered client type, for the schema and for masking secrets.</param>
    public DownloadClientController(IDownloadClientService clients, IEnumerable<IDownloadClientType> types)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(types);

        _clients = clients;
        _types = [.. types];
    }

    /// <summary>Lists every download client, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<DownloadClientResource>>> GetDownloadClients(
        CancellationToken cancellationToken)
    {
        var clients = await _clients.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(clients.Select(ToResource).ToList());
    }

    /// <summary>Reads one download client.</summary>
    /// <param name="id">The download client id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<DownloadClientResource>> GetDownloadClient(
        long id,
        CancellationToken cancellationToken)
    {
        var client = await _clients.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return client is null ? NotFound() : Ok(ToResource(client));
    }

    /// <summary>
    /// Describes every client type and the settings form it wants, which is what the UI renders the
    /// download client editor from.
    /// </summary>
    [HttpGet("schema")]
    [Produces("application/json")]
    public ActionResult<List<DownloadClientSchemaResource>> GetSchema() =>
        Ok(_types
            .Select(type => new DownloadClientSchemaResource(
                type.Type,
                type.Protocol.ToString(),
                [.. type.Fields.Select(field => new DownloadClientFieldResource(
                    field.Name,
                    field.Label,
                    field.Type,
                    field.Required,
                    field.HelpText,
                    field.Options,
                    field.Secret,
                    field.Advanced))]))
            .ToList());

    /// <summary>Adds a download client.</summary>
    /// <param name="resource">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<DownloadClientResource>> AddDownloadClient(
        [FromBody] DownloadClientInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await _clients
                .CreateAsync(DownloadClientResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetDownloadClient), new { id = client.Id }, ToResource(client));
        }
        catch (DownloadClientValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Replaces a download client's settings.</summary>
    /// <param name="id">The download client id.</param>
    /// <param name="resource">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<DownloadClientResource>> UpdateDownloadClient(
        long id,
        [FromBody] DownloadClientInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await _clients
                .UpdateAsync(id, DownloadClientResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return client is null ? NotFound() : Ok(ToResource(client));
        }
        catch (DownloadClientValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>
    /// Deletes a download client. Followed by <c>200</c> with an empty body, as the other controllers
    /// do — unless an indexer still names it, which answers <c>409</c> naming the indexer.
    /// </summary>
    /// <param name="id">The download client id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteDownloadClient(long id, CancellationToken cancellationToken)
    {
        try
        {
            return await _clients.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                ? Ok()
                : NotFound();
        }
        catch (DownloadClientInUseException exception)
        {
            return Problem(
                title: "Download client in use",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Connects with the draft's settings, so the user can check a client without waiting for a
    /// grab. A body carrying an <c>id</c> keeps that row's stored secrets where the settings carry
    /// the mask back. A type that cannot connect answers <c>200</c> with the failure, never a <c>500</c>.
    /// </summary>
    /// <param name="resource">The download client to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    [HttpPost("test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ProviderTestResource>> TestDownloadClient(
        [FromBody] DownloadClientInputResource resource,
        CancellationToken cancellationToken)
    {
        Core.Sources.ProviderTestResult result;

        try
        {
            result = await _clients
                .TestAsync(DownloadClientResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DownloadClientValidationException exception)
        {
            return Invalid(exception);
        }

        return Ok(new ProviderTestResource(result.Success, result.Error));
    }

    /// <summary>The resource for a row, with every secret masked.</summary>
    private DownloadClientResource ToResource(Core.Domain.DownloadClient client) =>
        DownloadClientResourceMapper.ToResource(client, TypeFor(client.Type));

    /// <summary>The client type a name names, or <see langword="null"/> when it is gone.</summary>
    private IDownloadClientType? TypeFor(string type) =>
        _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Turns the service's validation problem into the RFC 7807 body the API returns.</summary>
    private ActionResult Invalid(DownloadClientValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }
}
