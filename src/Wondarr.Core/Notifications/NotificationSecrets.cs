using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wondarr.Core.Notifications;

/// <summary>
/// Masking and keeping of a provider's secret settings. A secret is never handed back to the API and
/// never logged: reading a notification replaces it with <see cref="Mask"/>, and a write that carries
/// <see cref="Mask"/> back keeps whatever is stored.
/// </summary>
public static class NotificationSecrets
{
    /// <summary>What the API shows in place of a stored secret.</summary>
    public const string Mask = "********";

    /// <summary>
    /// Copies <paramref name="settings"/> with every secret field that has a value replaced by
    /// <see cref="Mask"/>.
    /// </summary>
    /// <param name="settings">The stored settings.</param>
    /// <param name="fields">The provider's fields, which say what is secret.</param>
    public static JsonNode Masked(JsonElement settings, IReadOnlyList<NotificationField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var copy = Copy(settings);

        foreach (var field in fields)
        {
            if (!field.Secret)
            {
                continue;
            }

            if (copy[field.Name] is { } value && HasValue(value))
            {
                copy[field.Name] = Mask;
            }
        }

        return copy;
    }

    /// <summary>
    /// Merges an incoming settings object over what is stored: a secret field carrying exactly
    /// <see cref="Mask"/> keeps the stored value, and everything else the caller sent wins.
    /// </summary>
    /// <param name="incoming">The settings the caller sent.</param>
    /// <param name="stored">The settings on the row, as JSON text, or <see langword="null"/> for a new row.</param>
    /// <param name="fields">The provider's fields, which say what is secret.</param>
    public static string Merge(JsonElement incoming, string? stored, IReadOnlyList<NotificationField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var merged = Copy(incoming);
        var existing = Parse(stored);

        foreach (var field in fields)
        {
            if (!field.Secret)
            {
                continue;
            }

            if (merged[field.Name] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                string.Equals(text, Mask, StringComparison.Ordinal))
            {
                // The mask stands for what is stored; with nothing stored it stands for nothing, and
                // must never become the secret itself.
                if (existing?[field.Name] is { } kept)
                {
                    merged[field.Name] = kept.DeepClone();
                }
                else
                {
                    merged.Remove(field.Name);
                }
            }
        }

        return merged.ToJsonString();
    }

    /// <summary>
    /// Reads a settings (or events) column back as a JSON element that outlives the parse; an absent or
    /// unreadable column is an empty object.
    /// </summary>
    /// <param name="json">The column's text, or <see langword="null"/>.</param>
    public static JsonElement Read(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);

                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Fall through to the empty object.
            }
        }

        using var empty = JsonDocument.Parse("{}");

        return empty.RootElement.Clone();
    }

    private static JsonObject Copy(JsonElement settings) =>
        Parse(settings.ValueKind == JsonValueKind.Object ? settings.GetRawText() : "{}") ?? [];

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool HasValue(JsonNode node) => node switch
    {
        null => false,
        JsonValue value when value.TryGetValue<string>(out var text) => !string.IsNullOrEmpty(text),
        JsonValue => true,
        _ => true,
    };
}
