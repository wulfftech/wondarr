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

    /// <summary>The words that make a settings key look like a secret when no field definition says so.</summary>
    private static readonly string[] SecretWords = ["key", "token", "password", "secret", "passkey", "cookie", "auth"];

    /// <summary>
    /// Whether a settings key probably holds a secret: its name contains, in any case, <c>key</c>,
    /// <c>token</c>, <c>password</c>, <c>secret</c>, <c>passkey</c>, <c>cookie</c> or <c>auth</c>.
    /// </summary>
    /// <param name="name">The settings key.</param>
    public static bool LooksSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// Copies <paramref name="settings"/> with every secret field that has a value replaced by
    /// <see cref="Mask"/>.
    /// </summary>
    /// <param name="settings">The stored settings.</param>
    /// <param name="fields">
    /// The provider's fields, which say what is secret, or <see langword="null"/> when the provider
    /// is no longer registered. Then nothing says which key is secret, so every key whose name
    /// suggests one (<see cref="LooksSecret"/>) is masked.
    /// </param>
    public static JsonNode Masked(JsonElement settings, IReadOnlyList<NotificationField>? fields)
    {
        var copy = Copy(settings);

        if (fields is null)
        {
            foreach (var name in copy.Select(pair => pair.Key).ToList())
            {
                if (LooksSecret(name) && copy[name] is { } value && HasValue(value))
                {
                    copy[name] = Mask;
                }
            }

            return copy;
        }

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
    /// <see cref="Mask"/> keeps the stored value, and everything else the caller sent wins. A stored
    /// secret is kept only for the target it was saved for (see <see cref="TargetOf"/>): when the
    /// incoming settings point somewhere else, the caller must send the secret itself.
    /// </summary>
    /// <param name="incoming">The settings the caller sent.</param>
    /// <param name="stored">The settings on the row, as JSON text, or <see langword="null"/> for a new row.</param>
    /// <param name="fields">The provider's fields, which say what is secret.</param>
    /// <exception cref="SecretReentryException">A masked secret would be sent to a target other than the one it was stored for.</exception>
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
                    if (HasValue(kept) && !string.Equals(TargetOf(merged, fields), TargetOf(existing, fields), StringComparison.Ordinal))
                    {
                        throw new SecretReentryException(field.Name, field.Label);
                    }

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
    /// The normalised place the settings send to: the fields the provider flags
    /// <see cref="NotificationField.Target"/> (a URL, or the host, port and SSL flag a form splits it
    /// into), a URL reduced to scheme, host and port, everything lower-cased. A URL
    /// base or path is not part of it, because it stays on the same host. A field that is itself a
    /// secret (a Discord webhook URL) is never part of the target. Two settings objects with the same
    /// target may share a stored secret.
    /// </summary>
    /// <param name="settings">The settings, as stored or as sent.</param>
    /// <param name="fields">The provider's fields, which say what is secret.</param>
    public static string TargetOf(JsonObject settings, IReadOnlyList<NotificationField> fields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fields);

        var parts = new List<string>();

        foreach (var field in fields.Where(field => field.Target && !field.Secret))
        {
            var raw = TextOf(settings[field.Name]);

            parts.Add(field.Type == "checkbox"
                ? string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ? "ssl" : "plain"
                : UrlTarget(raw));
        }

        return string.Join('|', parts);
    }

    private static string TextOf(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => string.Empty,
    };

    /// <summary>
    /// The scheme, host and port of an address, lower-cased with the default port spelled out, so two
    /// spellings of one server compare equal. A URL base or path is dropped. Text that is not an
    /// absolute URL is trimmed and lower-cased.
    /// </summary>
    /// <param name="raw">The address as the user typed it.</param>
    public static string UrlTarget(string? raw)
    {
        raw ??= string.Empty;

        var text = raw.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            return string.Concat(
                uri.Scheme.ToLowerInvariant(),
                "://",
                uri.IdnHost.ToLowerInvariant(),
                ":",
                uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToLowerInvariant();
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

/// <summary>
/// A masked secret cannot stand for the stored one because the address it would be sent to changed.
/// The message names the field and never the secret.
/// </summary>
public sealed class SecretReentryException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SecretReentryException"/> class.</summary>
    /// <param name="field">The settings key of the secret.</param>
    /// <param name="label">What the form calls it.</param>
    public SecretReentryException(string field, string label)
        : base(string.Concat("Enter the ", label, " again: the address changed."))
    {
        Field = field;
    }

    /// <summary>Gets the settings key of the secret, which is where the message belongs.</summary>
    public string Field { get; }
}
