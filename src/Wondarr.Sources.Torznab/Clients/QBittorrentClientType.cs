using System.Text.Json;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// The qBittorrent download client type: the settings form the UI renders, the rules those settings
/// are stored under, and the connection test that also checks Wondarr can see where the client saves.
/// </summary>
public sealed class QBittorrentClientType : IDownloadClientType
{
    private readonly QBittorrentProxy _proxy;

    /// <summary>Initialises a new instance of the <see cref="QBittorrentClientType"/> class.</summary>
    /// <param name="proxy">The qBittorrent HTTP layer.</param>
    public QBittorrentClientType(QBittorrentProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        _proxy = proxy;
    }

    /// <inheritdoc />
    public string Type => "qbittorrent";

    /// <inheritdoc />
    public DownloadProtocol Protocol => DownloadProtocol.Torrent;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("host", "Host", "text", true, "The host qBittorrent's Web UI listens on, for example 192.168.1.2"),
        new("port", "Port", "number", false, $"The port the Web UI listens on; the default is {QBittorrentSettings.DefaultPort}"),
        new("useSsl", "Use SSL", "checkbox", false, "Whether the Web UI is served over HTTPS"),
        new("urlBase", "URL base", "text", false, "The Web UI's URL base, when qBittorrent serves it under one", Advanced: true),
        new("username", "Username", "text", false, "The Web UI username; empty when qBittorrent needs no login"),
        new("password", "Password", "password", false, "The Web UI password", Secret: true),
        new("category", "Category", "text", false, $"The category Wondarr's torrents are filed under; the default is {QBittorrentSettings.DefaultCategory}"),
        new("remotePathMappings", "Remote path mappings", "keyValueList", false, "The client's path on the left, the same path as Wondarr sees it on the right"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = QBittorrentSettings.FromJson(settings);
        var messages = new List<string>();

        if (parsed.Host.Trim().Length == 0)
        {
            messages.Add("A host is required.");
        }

        if (parsed.Port is < 1 or > 65535)
        {
            messages.Add("The port must be between 1 and 65535.");
        }

        if (parsed.Category.Contains('/'))
        {
            messages.Add("The category must not contain '/'.");
        }

        foreach (var mapping in parsed.RemotePathMappings)
        {
            if (!IsAbsolute(mapping.Remote))
            {
                messages.Add($"The remote path '{mapping.Remote}' of a remote path mapping must be absolute.");
            }

            if (!IsAbsolute(mapping.Local))
            {
                messages.Add($"The local path '{mapping.Local}' of a remote path mapping must be absolute.");
            }
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var parsed = QBittorrentSettings.FromJson(settings);
        var session = _proxy.Session(QBittorrentProxy.SessionKey(0, settings.GetRawText()));

        try
        {
            // The version call also logs in when qBittorrent wants a login.
            var version = await _proxy.GetApiVersionAsync(session, parsed, cancellationToken).ConfigureAwait(false);

            if (version < QBittorrentProxy.MinimumApiVersion)
            {
                return new ProviderTestResult(false, $"qBittorrent 4.5 or newer is needed; the Web API reports version {version}.");
            }

            var categories = await _proxy.GetCategoriesAsync(session, parsed, cancellationToken).ConfigureAwait(false);

            if (!categories.ContainsKey(parsed.Category))
            {
                await _proxy.CreateCategoryAsync(session, parsed, parsed.Category, cancellationToken).ConfigureAwait(false);
            }

            // Where the category's torrents land: the category's own save path when it has one,
            // qBittorrent's default otherwise.
            var remoteSavePath = categories.TryGetValue(parsed.Category, out var savePath) && savePath.Length > 0
                ? savePath
                : await _proxy.GetSavePathAsync(session, parsed, cancellationToken).ConfigureAwait(false);

            var localSavePath = RemotePathMapper.Map(remoteSavePath, parsed.RemotePathMappings);

            if (!Directory.Exists(localSavePath))
            {
                return new ProviderTestResult(
                    false,
                    $"qBittorrent saves to {remoteSavePath}, which Wondarr sees as {localSavePath} — that folder does not exist here; add a remote path mapping.");
            }

            return new ProviderTestResult(true, null);
        }
        catch (DownloadClientException exception)
        {
            return new ProviderTestResult(false, exception.Message);
        }
    }

    /// <summary>Absolute on either world: <c>/downloads</c>, <c>C:\downloads</c> or a UNC <c>\\server\share</c>.</summary>
    private static bool IsAbsolute(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (path.StartsWith('/') || path.StartsWith('\\'))
        {
            return true;
        }

        return path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '/' || path[2] == '\\');
    }
}
