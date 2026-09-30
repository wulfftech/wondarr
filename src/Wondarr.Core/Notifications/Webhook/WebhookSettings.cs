// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookSettings.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookMethod.cs, GPL-3.0.
// Adapted for Wondarr: read from a `System.Text.Json` object rather than model-bound, no FluentValidation,
// and a typed header pair instead of Lidarr's `KeyValuePair<string, string>`.

using System.Text.Json;

namespace Wondarr.Core.Notifications.Webhook;

/// <summary>Which HTTP method a webhook sends with. Lidarr's two choices, kept as text.</summary>
internal static class WebhookMethods
{
    public const string Post = "POST";
    public const string Put = "PUT";

    /// <summary>Every method the settings form offers.</summary>
    public static readonly IReadOnlyList<string> All = [Post, Put];
}

/// <summary>One custom header the user asked for.</summary>
/// <param name="Key">The header name.</param>
/// <param name="Value">The header value.</param>
internal sealed record WebhookHeader(string Key, string Value);

/// <summary>One webhook notification's settings, read out of the stored JSON object.</summary>
/// <param name="Url">Where to send, or <see langword="null"/> when the user left it empty.</param>
/// <param name="Method">POST or PUT.</param>
/// <param name="Username">The basic-auth user, or <see langword="null"/>.</param>
/// <param name="Password">The basic-auth password, or <see langword="null"/>.</param>
/// <param name="Headers">The custom headers, in the order the user gave them.</param>
internal sealed record WebhookSettings(
    string? Url,
    string Method,
    string? Username,
    string? Password,
    IReadOnlyList<WebhookHeader> Headers)
{
    /// <summary>Reads a settings object; anything absent takes its default.</summary>
    /// <param name="settings">The stored settings object.</param>
    public static WebhookSettings From(JsonElement settings)
    {
        var url = Text(settings, "url");
        var method = Text(settings, "method");

        return new WebhookSettings(
            url,
            string.IsNullOrWhiteSpace(method) ? WebhookMethods.Post : method.Trim().ToUpperInvariant(),
            Text(settings, "username"),
            Text(settings, "password"),
            ReadHeaders(settings));
    }

    private static string? Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<WebhookHeader> ReadHeaders(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pairs = new List<WebhookHeader>();

        foreach (var entry in headers.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = entry.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
                ? keyValue.GetString()
                : null;

            var value = entry.TryGetProperty("value", out var headerValue) && headerValue.ValueKind == JsonValueKind.String
                ? headerValue.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(key))
            {
                pairs.Add(new WebhookHeader(key.Trim(), value ?? string.Empty));
            }
        }

        return pairs;
    }
}
