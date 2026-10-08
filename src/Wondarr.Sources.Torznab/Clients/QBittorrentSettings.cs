using System.Text.Json;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// One qBittorrent client row's settings, read from its JSON column. Absent values fall back to
/// the defaults the settings form shows.
/// </summary>
/// <param name="Host">The host qBittorrent listens on.</param>
/// <param name="Port">The port qBittorrent listens on.</param>
/// <param name="UseSsl">Whether the Web UI is served over HTTPS.</param>
/// <param name="UrlBase">The Web UI's URL base, normalized to <c>/base</c> form (leading slash, no trailing slash).</param>
/// <param name="Username">The Web UI username, or empty when qBittorrent needs no login.</param>
/// <param name="Password">The Web UI password; a secret, never logged.</param>
/// <param name="Category">The category Wondarr's torrents are filed under.</param>
/// <param name="RemotePathMappings">How the client's paths read on this host.</param>
public sealed record QBittorrentSettings(
    string Host,
    int Port,
    bool UseSsl,
    string UrlBase,
    string Username,
    string Password,
    string Category,
    IReadOnlyList<RemotePathMapping> RemotePathMappings)
{
    /// <summary>The port the settings form offers when the user types none.</summary>
    public const int DefaultPort = 8080;

    /// <summary>The category the settings form offers when the user types none.</summary>
    public const string DefaultCategory = "wondarr";

    /// <summary>Gets the base URL every request is built on, for example <c>http://192.168.1.2:8080/qbittorrent</c>.</summary>
    public string BaseUrl =>
        string.Concat(UseSsl ? "https" : "http", "://", Host, ":", Port.ToString(), UrlBase);

    /// <summary>Reads the settings out of a client row's (or a draft's) settings object.</summary>
    /// <param name="settings">The settings JSON.</param>
    /// <returns>The settings, with defaults where the object says nothing.</returns>
    public static QBittorrentSettings FromJson(JsonElement settings)
    {
        var host = String(settings, "host");
        var urlBase = String(settings, "urlBase").Trim().Trim('/');

        return new QBittorrentSettings(
            host,
            Integer(settings, "port") ?? DefaultPort,
            Boolean(settings, "useSsl"),
            urlBase.Length == 0 ? string.Empty : "/" + urlBase,
            String(settings, "username"),
            String(settings, "password"),
            String(settings, "category") is { Length: > 0 } category ? category : DefaultCategory,
            Mappings(settings));
    }

    /// <summary>
    /// Reads the <c>remotePathMappings</c> field: the JSON array of <c>{ "key": remote, "value": local }</c>
    /// pairs the <c>keyValueList</c> control renders.
    /// </summary>
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
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

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
