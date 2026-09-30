using System.Text.Json;

namespace Wondarr.Core.Notifications;

/// <summary>
/// One kind of notification endpoint. Implementations are registered as singletons and are stateless:
/// everything a send needs arrives in its arguments.
/// </summary>
public interface INotificationProvider
{
    /// <summary>Gets the implementation name stored in <see cref="Domain.Notification.Type"/>, for example <c>Webhook</c>.</summary>
    string Implementation { get; }

    /// <summary>Gets the fields of the settings form the UI renders.</summary>
    IReadOnlyList<NotificationField> Fields { get; }

    /// <summary>
    /// Checks a settings object. Returns the human messages to show the user, empty when the settings
    /// are usable.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    IReadOnlyList<string> Validate(JsonElement settings);

    /// <summary>Sends one message.</summary>
    /// <param name="message">What to send.</param>
    /// <param name="settings">The notification's settings.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendAsync(NotificationMessage message, JsonElement settings, CancellationToken cancellationToken);
}

/// <summary>
/// One field of a provider's settings form.
/// </summary>
/// <param name="Name">The settings key.</param>
/// <param name="Label">What the form calls it.</param>
/// <param name="Type">The control to render: <c>text</c>, <c>url</c>, <c>password</c>, <c>select</c>, <c>number</c>, <c>checkbox</c> or <c>keyValueList</c>.</param>
/// <param name="Required">Whether the notification is unusable without it.</param>
/// <param name="HelpText">A sentence shown under the control, or <see langword="null"/>.</param>
/// <param name="Options">The choices of a <c>select</c>, or <see langword="null"/>.</param>
/// <param name="Secret">Whether the value is masked on read and kept when the update sends the mask back.</param>
/// <param name="Advanced">Whether the control belongs in the form's advanced section.</param>
public sealed record NotificationField(
    string Name,
    string Label,
    string Type,
    bool Required,
    string? HelpText = null,
    IReadOnlyList<string>? Options = null,
    bool Secret = false,
    bool Advanced = false);
