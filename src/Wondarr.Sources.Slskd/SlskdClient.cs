using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wondarr.Sources.Slskd;

/// <summary>What slskd said to a share rescan request.</summary>
public enum SlskdRescanOutcome
{
    /// <summary>slskd started a scan of its shared folders.</summary>
    Started,

    /// <summary>A scan was already running (409), so none was started.</summary>
    AlreadyScanning,
}

/// <summary>
/// Reads slskd's application state and asks it to rescan its shares. Deliberately narrow: the
/// supervisor needs to know whether slskd is up, logged in and waiting for a restart, and the share
/// rescanner needs slskd to notice files imported after it started.
/// </summary>
public interface ISlskdClient
{
    /// <summary>Reads <c>GET /api/v0/application</c>.</summary>
    Task<SlskdApplicationState> GetApplicationStateAsync(CancellationToken cancellationToken);

    /// <summary>Asks slskd to rescan its shared folders (<c>PUT /api/v0/shares</c>).</summary>
    Task<SlskdRescanOutcome> RescanSharesAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SlskdClient : ISlskdClient
{
    /// <summary>Header slskd authenticates API calls with.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    /// <summary>Path of the application-state endpoint (slskd's own API version, not Wondarr's).</summary>
    public const string ApplicationPath = "/api/v0/application";

    /// <summary>Path of the shares endpoint; a PUT starts a rescan.</summary>
    public const string SharesPath = "/api/v0/shares";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ISlskdEndpoint _endpoint;

    /// <summary>Initialises a new instance of the <see cref="SlskdClient"/> class.</summary>
    public SlskdClient(HttpClient http, ISlskdEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);

        _http = http;
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    public async Task<SlskdApplicationState> GetApplicationStateAsync(CancellationToken cancellationToken)
    {
        var (baseAddress, apiKey) = await _endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, ApplicationPath));
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // A 401 or a 503 is the supervisor's business; surface it as a plain HTTP failure.
        response.EnsureSuccessStatusCode();

        var state = await response.Content
            .ReadFromJsonAsync<SlskdApplicationState>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return state ?? new SlskdApplicationState();
    }

    /// <inheritdoc />
    public async Task<SlskdRescanOutcome> RescanSharesAsync(CancellationToken cancellationToken)
    {
        var (baseAddress, apiKey) = await _endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(baseAddress, SharesPath));
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return SlskdRescanOutcome.AlreadyScanning;
        }

        response.EnsureSuccessStatusCode();

        return SlskdRescanOutcome.Started;
    }
}
