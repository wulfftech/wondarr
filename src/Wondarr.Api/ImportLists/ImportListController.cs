using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Wondarr.Api.Notifications;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.ImportLists.Csv;
using Wondarr.Core.Jobs;

namespace Wondarr.Api.ImportLists;

/// <summary>
/// Import lists (ADR-0012): the synced lists — a CSV export, a playlist, a scrobble list — and the
/// pasted lists of the bulk add. A sync runs as an <c>ImportListSync</c> command; its items are read
/// through <c>/api/v1/importlist/item</c>, where unresolved ones are resolved or skipped.
/// </summary>
[ApiController]
[Route("api/v1/importlist")]
public sealed class ImportListController : ControllerBase
{
    /// <summary>Command bodies are camelCase, like the API's own resources.</summary>
    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IImportListService _lists;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="ImportListController"/> class.</summary>
    /// <param name="lists">The import-list service.</param>
    /// <param name="commands">The command queue a sync runs on.</param>
    public ImportListController(IImportListService lists, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(commands);

        _lists = lists;
        _commands = commands;
    }

    /// <summary>Lists the import lists, newest first.</summary>
    /// <param name="includePasted">Whether the bulk add's pasted lists are included (default false).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<ImportListResource>>> GetImportLists(
        [FromQuery] bool includePasted,
        CancellationToken cancellationToken)
    {
        var lists = await _lists.ListAsync(includePasted, cancellationToken).ConfigureAwait(false);

        return Ok(lists.Select(view => view.ToResource(ProviderFor(view.List.Type))).ToList());
    }

    /// <summary>Reads one list with its counts.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<ImportListResource>> GetImportList(long id, CancellationToken cancellationToken)
    {
        var view = await _lists.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return view is null ? NotFound() : Ok(view.ToResource(ProviderFor(view.List.Type)));
    }

    /// <summary>The providers a list can be created from, with their settings forms.</summary>
    [HttpGet("schema")]
    [Produces("application/json")]
    public ActionResult<List<ImportListSchemaResource>> GetSchema() =>
        Ok(_lists.Providers
            .Select(provider => new ImportListSchemaResource(
                provider.Type,
                provider.DisplayName,
                [.. provider.Fields.Select(field => new NotificationFieldResource(
                    field.Name,
                    field.Label,
                    field.Type,
                    field.Required,
                    field.HelpText,
                    field.Options,
                    field.Secret,
                    field.Advanced))]))
            .ToList());

    /// <summary>Creates a synced list. Nothing is read until it is synced.</summary>
    /// <param name="resource">The list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ImportListResource>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportListResource>> AddImportList(
        [FromBody] ImportListInputResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var list = await _lists.CreateAsync(ToDraft(resource), cancellationToken).ConfigureAwait(false);
            var view = await _lists.GetAsync(list.Id, cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(GetImportList), new { id = list.Id }, view!.ToResource(ProviderFor(list.Type)));
        }
        catch (ImportListValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Changes a synced list; its items stay.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="resource">The new settings.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ImportListResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportListResource>> UpdateImportList(
        long id,
        [FromBody] ImportListInputResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var list = await _lists.UpdateAsync(id, ToDraft(resource), cancellationToken).ConfigureAwait(false);

            if (list is null)
            {
                return NotFound();
            }

            var view = await _lists.GetAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(view!.ToResource(ProviderFor(list.Type)));
        }
        catch (ImportListValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes a list and its items; the songs it added stay in the library.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteImportList(long id, CancellationToken cancellationToken) =>
        await _lists.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    /// <summary>Queues a sync of one list.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost("{id:long}/sync")]
    [Produces("application/json")]
    [ProducesResponseType<ImportListSyncAcceptedResource>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncImportList(long id, CancellationToken cancellationToken)
    {
        var view = await _lists.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (view is null)
        {
            return NotFound();
        }

        if (view.List.Type == ImportList.PasteType)
        {
            ModelState.AddModelError("id", "A pasted list is resolved once and has no source to sync.");

            return ValidationProblem(ModelState);
        }

        var command = await _commands
            .EnqueueAsync(
                ImportListSyncCommandHandler.CommandName,
                JsonSerializer.Serialize(
                    new { name = ImportListSyncCommandHandler.CommandName, importListId = id },
                    CommandJson),
                CommandTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new ImportListSyncAcceptedResource(command.Id));
    }

    /// <summary>
    /// Reads a CSV file the way a CSV list would — its format, header, row count, first rows and any
    /// problem — without storing anything. The UI calls it before creating the list and while the
    /// user maps columns.
    /// </summary>
    /// <param name="resource">The file's text and an optional column mapping.</param>
    [HttpPost("csv/preview")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<CsvPreviewResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<CsvPreviewResource> PreviewCsv([FromBody] CsvPreviewInputResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (string.IsNullOrWhiteSpace(resource.SourceText))
        {
            ModelState.AddModelError("sourceText", "Send the CSV file's text.");

            return ValidationProblem(ModelState);
        }

        if (resource.SourceText.Length > ImportListService.MaxSourceTextLength)
        {
            ModelState.AddModelError(
                "sourceText",
                $"The file is too large: at most {ImportListService.MaxSourceTextLength / (1024 * 1024)} MB.");

            return ValidationProblem(ModelState);
        }

        using var settings = JsonDocument.Parse(resource.Settings?.ToJsonString() ?? "{}");
        var preview = CsvImportListProvider.Preview(resource.SourceText, settings.RootElement);

        return Ok(new CsvPreviewResource(
            preview.Format,
            preview.Headers,
            preview.RowCount,
            [.. preview.Sample.Select(entry => entry.ToResource())],
            preview.Problems));
    }

    private static ImportListDraft ToDraft(ImportListInputResource resource)
    {
        var settings = resource.Settings is null
            ? default
            : JsonDocument.Parse(resource.Settings.ToJsonString()).RootElement.Clone();

        return new ImportListDraft(
            resource.Type ?? string.Empty,
            resource.Name ?? string.Empty,
            settings,
            resource.SourceText,
            resource.Policy,
            resource.QualityProfileId,
            resource.LibraryId,
            resource.Enabled ?? true,
            resource.SyncIntervalHours ?? 24);
    }

    private IImportListProvider? ProviderFor(string type) =>
        _lists.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Type, type, StringComparison.OrdinalIgnoreCase));

    private ActionResult Invalid(ImportListValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }
}
