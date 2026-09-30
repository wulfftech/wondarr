using System.Text.Json;

namespace Wondarr.Core.Plex;

/// <summary>The Plex Media Server endpoints: identity, libraries, partial scans and empty trash.</summary>
public interface IPlexServerClient
{
    /// <summary>Reads <c>/identity</c>: the machine identifier and version, which need no token on a LAN.</summary>
    /// <param name="server">The server URL, which may carry a path prefix behind a reverse proxy.</param>
    /// <param name="token">The token to send.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<PlexIdentity> GetIdentityAsync(Uri server, string token, CancellationToken cancellationToken);

    /// <summary>Lists the server's libraries.</summary>
    /// <param name="server">The server URL.</param>
    /// <param name="token">The token to send.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<PlexSection>> GetSectionsAsync(Uri server, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the server to scan one folder. Plex answers immediately and scans in the background, so
    /// this returns as soon as the request is accepted.
    /// </summary>
    /// <param name="server">The server URL.</param>
    /// <param name="token">The token to send.</param>
    /// <param name="sectionKey">The section to scan.</param>
    /// <param name="serverPath">The folder as the server sees it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task RefreshPathAsync(
        Uri server,
        string token,
        string sectionKey,
        string serverPath,
        CancellationToken cancellationToken);

    /// <summary>Empties the section's trash, purging the items a scan found missing.</summary>
    /// <param name="server">The server URL.</param>
    /// <param name="token">The token to send.</param>
    /// <param name="sectionKey">The section to empty.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task EmptyTrashAsync(Uri server, string token, string sectionKey, CancellationToken cancellationToken);

    /// <summary>Reads a section's <c>refreshing</c> flag, for waiting out a scan.</summary>
    /// <param name="server">The server URL.</param>
    /// <param name="token">The token to send.</param>
    /// <param name="sectionKey">The section to ask about.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="false"/> when the section is gone, which is not an error here.</returns>
    Task<bool> IsRefreshingAsync(Uri server, string token, string sectionKey, CancellationToken cancellationToken);
}

/// <summary>
/// The Plex Media Server client. The server URL is a parameter rather than the client's base address,
/// because Wondarr talks to whichever server the user selected. Certificates are validated normally:
/// <c>plex.direct</c> names carry a valid certificate, and a self-signed one must be fixed, not ignored.
/// </summary>
public sealed class PlexServerClient : IPlexServerClient
{
    /// <summary>The name of the <see cref="IHttpClientFactory"/> client this uses.</summary>
    public const string ClientName = "plex-server";

    private const string Subject = "The Plex server";

    private readonly IHttpClientFactory _factory;
    private readonly IPlexClientIdentifier _identifier;

    /// <summary>Initialises a new instance of the <see cref="PlexServerClient"/> class.</summary>
    /// <param name="factory">The factory holding the <c>plex-server</c> client.</param>
    /// <param name="identifier">The stable client identifier every request is stamped with.</param>
    public PlexServerClient(IHttpClientFactory factory, IPlexClientIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(identifier);

        _factory = factory;
        _identifier = identifier;
    }

    /// <inheritdoc />
    public async Task<PlexIdentity> GetIdentityAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken)
    {
        var request = await NewRequestAsync(HttpMethod.Get, server, "identity", token, cancellationToken)
            .ConfigureAwait(false);

        using (request)
        {
            var body = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            using var document = PlexHttp.Parse(body, request, Subject);
            var container = PlexJson.Child(document.RootElement, "MediaContainer") ?? document.RootElement;

            return new PlexIdentity(
                MachineIdentifier: PlexJson.Text(container, "machineIdentifier") ?? string.Empty,
                Version: PlexJson.Text(container, "version") ?? string.Empty,
                Claimed: PlexJson.Flag(container, "claimed"));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlexSection>> GetSectionsAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken)
    {
        var request = await NewRequestAsync(HttpMethod.Get, server, "library/sections", token, cancellationToken)
            .ConfigureAwait(false);

        using (request)
        {
            var body = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            using var document = PlexHttp.Parse(body, request, Subject);

            return ReadSections(document.RootElement);
        }
    }

    /// <inheritdoc />
    public async Task RefreshPathAsync(
        Uri server,
        string token,
        string sectionKey,
        string serverPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);

        var relative = $"library/sections/{Uri.EscapeDataString(sectionKey)}/refresh"
            + $"?path={Uri.EscapeDataString(serverPath)}";

        var request = await NewRequestAsync(HttpMethod.Get, server, relative, token, cancellationToken)
            .ConfigureAwait(false);

        using (request)
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EmptyTrashAsync(
        Uri server,
        string token,
        string sectionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionKey);

        var relative = $"library/sections/{Uri.EscapeDataString(sectionKey)}/emptyTrash";

        var request = await NewRequestAsync(HttpMethod.Put, server, relative, token, cancellationToken)
            .ConfigureAwait(false);

        using (request)
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsRefreshingAsync(
        Uri server,
        string token,
        string sectionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionKey);

        var sections = await GetSectionsAsync(server, token, cancellationToken).ConfigureAwait(false);

        return sections
            .FirstOrDefault(section => string.Equals(section.Key, sectionKey, StringComparison.Ordinal))
            ?.Refreshing ?? false;
    }

    /// <summary>
    /// Builds one request against a server URL. The URL is treated as a directory, so a prefix such as
    /// <c>https://host/plex</c> survives the relative path.
    /// </summary>
    private async Task<HttpRequestMessage> NewRequestAsync(
        HttpMethod method,
        Uri server,
        string relative,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var clientIdentifier = await _identifier.GetAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, new Uri(AsDirectory(server), relative));

        PlexClientHeaders.Apply(request, clientIdentifier, token);

        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = _factory.CreateClient(ClientName);
        var response = await PlexHttp
            .SendAsync(http, request, Subject, cancellationToken)
            .ConfigureAwait(false);

        return PlexHttp.Read(response, request, Subject);
    }

    /// <summary>Adds the trailing slash that makes relative resolution keep a proxy's path prefix.</summary>
    private static Uri AsDirectory(Uri server)
    {
        var absolute = server.AbsoluteUri;

        return absolute.EndsWith('/') ? server : new Uri(absolute + "/", UriKind.Absolute);
    }

    private static IReadOnlyList<PlexSection> ReadSections(JsonElement root)
    {
        var container = PlexJson.Child(root, "MediaContainer") ?? root;

        return
        [
            .. PlexJson.Items(PlexJson.Child(container, "Directory")).Select(directory => new PlexSection(
                Key: PlexJson.TextOrNumber(directory, "key") ?? string.Empty,
                Title: PlexJson.Text(directory, "title") ?? string.Empty,
                Type: PlexJson.Text(directory, "type") ?? string.Empty,
                Refreshing: PlexJson.Flag(directory, "refreshing"),
                Locations:
                [
                    .. PlexJson.Items(PlexJson.Child(directory, "Location"))
                        .Select(location => PlexJson.Text(location, "path") ?? string.Empty),
                ])),
        ];
    }
}
