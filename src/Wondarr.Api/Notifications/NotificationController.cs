using Wondarr.Core.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Notifications;

/// <summary>
/// The notifications Wondarr sends when something happens (ARCHITECTURE §5.7). One row per endpoint:
/// which provider it is, its settings and the events it wants. The event values, the settings masking
/// and the send paths all live in <see cref="INotificationService"/> and the providers.
/// </summary>
[ApiController]
[Route("api/v1/notification")]
public sealed class NotificationController : ControllerBase
{
    private readonly INotificationService _notifications;
    private readonly IReadOnlyList<INotificationProvider> _providers;

    /// <summary>Initialises a new instance of the <see cref="NotificationController"/> class.</summary>
    /// <param name="notifications">The notification store.</param>
    /// <param name="providers">Every registered provider, for the schema and for masking secrets.</param>
    public NotificationController(
        INotificationService notifications,
        IEnumerable<INotificationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(providers);

        _notifications = notifications;
        _providers = [.. providers];
    }

    /// <summary>Lists every notification, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<NotificationResource>>> GetNotifications(CancellationToken cancellationToken)
    {
        var notifications = await _notifications.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(notifications.Select(ToResource).ToList());
    }

    /// <summary>Reads one notification.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<NotificationResource>> GetNotification(long id, CancellationToken cancellationToken)
    {
        var notification = await _notifications.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return notification is null ? NotFound() : Ok(ToResource(notification));
    }

    /// <summary>
    /// Describes every provider and the settings form it wants, which is what the UI renders the
    /// notification editor from.
    /// </summary>
    [HttpGet("schema")]
    [Produces("application/json")]
    public ActionResult<List<NotificationSchemaResource>> GetSchema() =>
        Ok(_providers
            .Select(provider => new NotificationSchemaResource(
                provider.Implementation,
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

    /// <summary>Adds a notification.</summary>
    /// <param name="resource">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<NotificationResource>> AddNotification(
        [FromBody] NotificationInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var notification = await _notifications
                .CreateAsync(NotificationResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetNotification), new { id = notification.Id }, ToResource(notification));
        }
        catch (NotificationValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Replaces a notification's settings and subscriptions.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="resource">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<NotificationResource>> UpdateNotification(
        long id,
        [FromBody] NotificationInputResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var notification = await _notifications
                .UpdateAsync(id, NotificationResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);

            return notification is null ? NotFound() : Ok(ToResource(notification));
        }
        catch (NotificationValidationException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes a notification. Followed by <c>200</c> with an empty body, as the other controllers do.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteNotification(long id, CancellationToken cancellationToken) =>
        await _notifications.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    /// <summary>
    /// Sends a test message through the draft's provider, so the user can check an endpoint without
    /// waiting for something to happen. A body carrying an <c>id</c> keeps that row's stored secrets
    /// where the settings carry the mask back.
    /// </summary>
    /// <param name="resource">The notification to test.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    [HttpPost("test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> TestNotification(
        [FromBody] NotificationInputResource resource,
        CancellationToken cancellationToken)
    {
        NotificationTestResult result;

        try
        {
            result = await _notifications
                .TestAsync(NotificationResourceMapper.ToDraft(resource), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotificationValidationException exception)
        {
            return Invalid(exception);
        }

        if (result.Success)
        {
            return Ok(new { });
        }

        ModelState.AddModelError("settings", result.Error ?? "The test message could not be sent.");

        return ValidationProblem(ModelState);
    }

    /// <summary>The resource for a row, with a delivery failure reported as a validation problem.</summary>
    private NotificationResource ToResource(Core.Domain.Notification notification) =>
        NotificationResourceMapper.ToResource(notification, ProviderFor(notification.Type));

    /// <summary>The provider an implementation name names, or <see langword="null"/> when it is gone.</summary>
    private INotificationProvider? ProviderFor(string implementation) =>
        _providers.FirstOrDefault(provider =>
            string.Equals(provider.Implementation, implementation, StringComparison.OrdinalIgnoreCase));

    /// <summary>Turns the service's validation problem into the RFC 7807 body the API returns.</summary>
    private ActionResult Invalid(NotificationValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }
}
