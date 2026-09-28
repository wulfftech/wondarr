using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Reads slskd's application state. Deliberately narrow: the supervisor needs to know whether
/// slskd is up, logged in and waiting for a restart, and nothing else.
/// </summary>
public interface ISlskdClient
{
    /// <summary>Reads <c>GET /api/v0/application</c>.</summary>
    Task<SlskdApplicationState> GetApplicationStateAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SlskdClient : ISlskdClient
{
    /// <summary>Header slskd authenticates API calls with.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    /// <summary>Path of the application-state endpoint (slskd's own API version, not Wondarr's).</summary>
    public const string ApplicationPath = "/api/v0/application";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SlskdSecretsStore _secrets;

    /// <summary>Initialises a new instance of the <see cref="SlskdClient"/> class.</summary>
    public SlskdClient(HttpClient http, IOptionsMonitor<SoulseekOptions> options, SlskdSecretsStore secrets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        _http = http;
        _secrets = secrets;

        // The bundled slskd is always on loopback; the port is the only part that moves.
        _http.BaseAddress ??= new Uri($"http://127.0.0.1:{options.CurrentValue.WebPort}/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<SlskdApplicationState> GetApplicationStateAsync(CancellationToken cancellationToken)
    {
        var secrets = await _secrets.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, ApplicationPath);
        request.Headers.Add(ApiKeyHeader, secrets.ApiKey);

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
}
