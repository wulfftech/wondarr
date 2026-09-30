using System.Globalization;
using System.Text.Json;

namespace Wondarr.Core.Plex;

/// <summary>
/// Small readers over a <see cref="JsonDocument"/>. Both Plex APIs answer with shapes Wondarr maps to
/// records, and both mix conventions — a section key is a string here and a number there — so the
/// readers are tolerant and every one of them has a safe answer for a missing or unexpected field.
/// </summary>
internal static class PlexJson
{
    /// <summary>Reads a nested object or array, or <see langword="null"/> when it is absent.</summary>
    public static JsonElement? Child(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var child)
            && child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return child;
        }

        return null;
    }

    /// <summary>Reads a string, or <see langword="null"/> when the field is absent or another kind.</summary>
    public static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a field Plex sends as either a string or a number, as a string. A section key is a JSON
    /// string on a real server and a number in some clients' fixtures.
    /// </summary>
    public static string? TextOrNumber(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Reads a number, or <paramref name="fallback"/> when the field is absent or another kind.</summary>
    public static long Number(JsonElement parent, string name, long fallback = 0)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }

        return fallback;
    }

    /// <summary>Reads a flag, or <paramref name="fallback"/> when the field is absent or another kind.</summary>
    public static bool Flag(JsonElement parent, string name, bool fallback = false)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }

        return fallback;
    }

    /// <summary>Reads an ISO-8601 instant, or <see langword="null"/> when the field is absent or unreadable.</summary>
    public static DateTimeOffset? Instant(JsonElement parent, string name)
    {
        var text = Text(parent, name);

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
                ? value
                : null;
    }

    /// <summary>Walks an array's items; an absent or non-array value yields nothing.</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().ToList() : [];
}
