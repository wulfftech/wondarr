using System.Globalization;
using System.Text.Json;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// One SABnzbd client row's settings, read from its JSON column. Absent values fall back to the
/// defaults the settings form shows.
/// </summary>
/// <param name="Host">The host SABnzbd listens on.</param>
/// <param name="Port">The port SABnzbd listens on.</param>
/// <param name="UseSsl">Whether SABnzbd is served over HTTPS.</param>
/// <param name="UrlBase">The URL base, normalized to <c>/base</c> form (leading slash, no trailing slash).</param>
/// <param name="ApiKey">The API key; a secret, never logged.</param>
/// <param name="Category">The category Wondarr's jobs are filed under.</param>
/// <param name="RemotePathMappings">How the client's paths read on this host.</param>
public sealed record SabnzbdSettings(
    string Host,
    int Port,
    bool UseSsl,
    string UrlBase,
    string ApiKey,
    string Category,
    IReadOnlyList<RemotePathMapping> RemotePathMappings)
{
    /// <summary>The port the settings form offers when the user types none.</summary>
    public const int DefaultPort = 8080;

    /// <summary>The category the settings form offers when the user types none.</summary>
    public const string DefaultCategory = "wondarr";

    /// <summary>Gets the API endpoint, for example <c>http://192.168.1.2:8080/sabnzbd/api</c>.</summary>
    public string ApiUrl =>
        string.Concat(UseSsl ? "https" : "http", "://", Host, ":", Port.ToString(CultureInfo.InvariantCulture), UrlBase, "/api");

    /// <summary>Reads the settings out of a client row's (or a draft's) settings object.</summary>
    /// <param name="settings">The settings JSON.</param>
    public static SabnzbdSettings FromJson(JsonElement settings)
    {
        var urlBase = String(settings, "urlBase").Trim().Trim('/');

        return new SabnzbdSettings(
            String(settings, "host").Trim(),
            Integer(settings, "port") ?? DefaultPort,
            Boolean(settings, "useSsl"),
            urlBase.Length == 0 ? string.Empty : "/" + urlBase,
            String(settings, "apiKey").Trim(),
            String(settings, "category") is { Length: > 0 } category ? category.Trim() : DefaultCategory,
            Mappings(settings));
    }

    /// <summary>The <c>remotePathMappings</c> field: <c>{ "key": remote, "value": local }</c> pairs, as P7-05 reads them.</summary>
    private static List<RemotePathMapping> Mappings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("remotePathMappings", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var mappings = new List<RemotePathMapping>();

        foreach (var entry in array.EnumerateArray())
        {
            var remote = String(entry, "key");
            var local = String(entry, "value");

            if (remote.Length > 0 && local.Length > 0)
            {
                mappings.Add(new RemotePathMapping(remote, local));
            }
        }

        return mappings;
    }

    private static string String(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? Integer(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool Boolean(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));
}
