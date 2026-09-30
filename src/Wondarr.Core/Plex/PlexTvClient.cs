using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Plex;

/// <summary>The plex.tv endpoints Wondarr needs: the sign-in PIN flow and the account's servers.</summary>
public interface IPlexTvClient
{
    /// <summary>Creates a sign-in PIN the user approves on plex.tv.</summary>
    /// <param name="clientIdentifier">The stable identifier this install signs in with.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The PIN, with the URL the user opens.</returns>
    Task<PlexPin> CreatePinAsync(string clientIdentifier, CancellationToken cancellationToken);

    /// <summary>Polls a PIN. Plex answers with a token once the user has approved the code.</summary>
    /// <param name="pinId">The PIN id returned by <see cref="CreatePinAsync"/>.</param>
    /// <param name="clientIdentifier">The same identifier the PIN was created with.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The PIN's state. A PIN plex.tv no longer knows about is reported as expired rather than as a
    /// failure: from the caller's point of view the user simply has to start again.
    /// </returns>
    Task<PlexPinStatus> CheckPinAsync(long pinId, string clientIdentifier, CancellationToken cancellationToken);

    /// <summary>Lists the Plex Media Servers the token can reach.</summary>
    /// <param name="token">The account token.</param>
    /// <param name="clientIdentifier">The stable identifier this install signs in with.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Only the resources that provide a server; devices are filtered out.</returns>
    Task<IReadOnlyList<PlexServer>> GetServersAsync(
        string token,
        string clientIdentifier,
        CancellationToken cancellationToken);
}

/// <summary>
/// The plex.tv client. Every call carries the identifying headers, and the token travels only in
/// <c>X-Plex-Token</c>.
/// </summary>
public sealed class PlexTvClient : IPlexTvClient
{
    private const string Subject = "plex.tv";

    private readonly HttpClient _http;
    private readonly PlexOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="PlexTvClient"/> class.</summary>
    /// <param name="http">The typed client, with <see cref="PlexOptions.PlexTvBaseUrl"/> as its base address.</param>
    /// <param name="options">The bound <c>plex</c> section.</param>
    /// <param name="timeProvider">The clock the PIN's expiry is judged against.</param>
    public PlexTvClient(HttpClient http, IOptions<PlexOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _http = http;
        _options = options.Value;
        _time = timeProvider;
    }

    /// <inheritdoc />
    public async Task<PlexPin> CreatePinAsync(string clientIdentifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientIdentifier);

        using var request = NewRequest(HttpMethod.Post, "api/v2/pins?strong=true", clientIdentifier, token: null);

        var body = PlexHttp.Read(
            await PlexHttp.SendAsync(_http, request, Subject, cancellationToken).ConfigureAwait(false),
            request,
            Subject);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var id = PlexJson.Number(root, "id");
        var code = PlexJson.Text(root, "code") ?? string.Empty;
        var expiresAt = PlexJson.Instant(root, "expiresAt") ?? DateTimeOffset.MaxValue;

        return new PlexPin(id, code, expiresAt, BuildAuthUrl(clientIdentifier, code));
    }

    /// <inheritdoc />
    public async Task<PlexPinStatus> CheckPinAsync(
        long pinId,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientIdentifier);

        using var request = NewRequest(
            HttpMethod.Get,
            $"api/v2/pins/{pinId.ToString(CultureInfo.InvariantCulture)}",
            clientIdentifier,
            token: null);

        var response = await PlexHttp
            .SendAsync(_http, request, Subject, cancellationToken)
            .ConfigureAwait(false);

        // A PIN plex.tv has forgotten reads as expired: the user has to start the sign-in again.
        if (response.Status == HttpStatusCode.NotFound)
        {
            return new PlexPinStatus(Expired: true, AuthToken: null);
        }

        var body = PlexHttp.Read(response, request, Subject);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var expiresAt = PlexJson.Instant(root, "expiresAt");
        var token = PlexJson.Text(root, "authToken");

        return new PlexPinStatus(
            Expired: expiresAt is { } expiry && expiry <= _time.GetUtcNow(),
            AuthToken: string.IsNullOrEmpty(token) ? null : token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlexServer>> GetServersAsync(
        string token,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientIdentifier);

        using var request = NewRequest(
            HttpMethod.Get,
            "api/v2/resources?includeHttps=1&includeRelay=1",
            clientIdentifier,
            token);

        var body = PlexHttp.Read(
            await PlexHttp.SendAsync(_http, request, Subject, cancellationToken).ConfigureAwait(false),
            request,
            Subject);

        using var document = JsonDocument.Parse(body);

        return [.. PlexJson.Items(document.RootElement).Where(IsServer).Select(ReadServer)];
    }

    /// <summary>The URL the user opens to approve a PIN.</summary>
    private string BuildAuthUrl(string clientIdentifier, string code) =>
        $"{_options.AppAuthUrl}#?clientID={Uri.EscapeDataString(clientIdentifier)}"
        + $"&code={Uri.EscapeDataString(code)}"
        + "&context%5Bdevice%5D%5Bproduct%5D=Wondarr";

    private static HttpRequestMessage NewRequest(
        HttpMethod method,
        string path,
        string clientIdentifier,
        string? token)
    {
        var request = new HttpRequestMessage(method, path);
        PlexClientHeaders.Apply(request, clientIdentifier, token);

        return request;
    }

    /// <summary>Whether a resource provides a media server; the account also holds players and clients.</summary>
    private static bool IsServer(JsonElement resource)
    {
        var provides = PlexJson.Text(resource, "provides");

        return provides is not null
            && provides.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("server", StringComparer.OrdinalIgnoreCase);
    }

    private static PlexServer ReadServer(JsonElement resource) =>
        new(
            Name: PlexJson.Text(resource, "name") ?? string.Empty,
            MachineIdentifier: PlexJson.Text(resource, "clientIdentifier") ?? string.Empty,
            Owned: PlexJson.Flag(resource, "owned"),
            AccessToken: PlexJson.Text(resource, "accessToken"),
            ProductVersion: PlexJson.Text(resource, "productVersion"),
            Connections: ReadConnections(resource));

    private static IReadOnlyList<PlexServerConnection> ReadConnections(JsonElement resource) =>
    [
        .. PlexJson.Items(PlexJson.Child(resource, "connections")).Select(connection => new PlexServerConnection(
            Uri: PlexJson.Text(connection, "uri") ?? string.Empty,
            Local: PlexJson.Flag(connection, "local"),
            Relay: PlexJson.Flag(connection, "relay"),
            Protocol: PlexJson.Text(connection, "protocol") ?? string.Empty,
            Address: PlexJson.Text(connection, "address") ?? string.Empty,
            Port: (int)PlexJson.Number(connection, "port"))),
    ];
}
