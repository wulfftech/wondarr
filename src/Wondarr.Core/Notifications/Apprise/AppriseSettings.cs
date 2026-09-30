// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Apprise/AppriseSettings.cs, GPL-3.0.
// Adapted for Wondarr: read from a `System.Text.Json` object rather than model-bound, no FluentValidation,
// and tags stored as the comma-separated text the settings form shows rather than a collection.

using System.Text.Json;

namespace Wondarr.Core.Notifications.Apprise;

/// <summary>One Apprise notification's settings, read out of the stored JSON object.</summary>
/// <param name="ServerUrl">The Apprise API server, or <see langword="null"/> when the user left it empty.</param>
/// <param name="ConfigurationKey">The persistent-storage key, or <see langword="null"/>.</param>
/// <param name="StatelessUrls">The comma-separated service URLs, or <see langword="null"/>.</param>
/// <param name="NotificationType">One of <see cref="AppriseNotificationTypes"/>.</param>
/// <param name="Tags">The tags to notify, in the order the user gave them.</param>
/// <param name="AuthUsername">The basic-auth user, or <see langword="null"/>.</param>
/// <param name="AuthPassword">The basic-auth password, or <see langword="null"/>.</param>
internal sealed record AppriseSettings(
    string? ServerUrl,
    string? ConfigurationKey,
    string? StatelessUrls,
    string NotificationType,
    IReadOnlyList<string> Tags,
    string? AuthUsername,
    string? AuthPassword)
{
    /// <summary>Reads a settings object; anything absent takes its default.</summary>
    /// <param name="settings">The stored settings object.</param>
    public static AppriseSettings From(JsonElement settings)
    {
        var type = Text(settings, "notificationType");

        return new AppriseSettings(
            Text(settings, "serverUrl"),
            Text(settings, "configurationKey"),
            Text(settings, "statelessUrls"),
            string.IsNullOrWhiteSpace(type) ? AppriseNotificationTypes.Info : type.Trim().ToLowerInvariant(),
            SplitTags(Text(settings, "tags")),
            Text(settings, "authUsername"),
            Text(settings, "authPassword"));
    }

    /// <summary>Gets a value indicating whether the notification sends through a persistent-storage key.</summary>
    public bool HasConfigurationKey => !string.IsNullOrWhiteSpace(ConfigurationKey);

    /// <summary>Gets a value indicating whether the notification sends through stateless URLs.</summary>
    public bool HasStatelessUrls => !string.IsNullOrWhiteSpace(StatelessUrls);

    /// <summary>
    /// Checks the server URL: absolute, http or https. Apprise API servers are commonly plain http on a
    /// private network, so unlike a webhook URL this one is not required to be https. Credentials in
    /// the URL are refused: the server URL is not a secret and is shown by the API, and basic
    /// authentication has its own (masked) fields.
    /// </summary>
    /// <param name="value">The URL the user entered, or <see langword="null"/>.</param>
    /// <param name="url">The parsed URL when it is usable.</param>
    public static bool TryServerUrl(string? value, out Uri url)
    {
        url = null!;

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            parsed.UserInfo.Length > 0)
        {
            return false;
        }

        url = parsed;

        return true;
    }

    /// <summary>Splits the comma-separated tags the form stores into the ones worth sending.</summary>
    private static List<string> SplitTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
