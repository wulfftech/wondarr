using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// slskd refused a search because too many are already in flight. It is not retried here: the
/// budget exists to keep Wondarr below the limit, so hitting it means something else is searching
/// the same slskd account (an external slskd, or a second Wondarr).
/// </summary>
public sealed class SlskdSearchRejectedException : Exception
{
    /// <summary>The message every instance carries.</summary>
    public const string DefaultMessage = "slskd refused the search: too many searches in flight";

    /// <summary>Initialises a new instance of the <see cref="SlskdSearchRejectedException"/> class.</summary>
    public SlskdSearchRejectedException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SlskdSearchRejectedException"/> class.</summary>
    /// <param name="message">The message to carry.</param>
    public SlskdSearchRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SlskdSearchRejectedException"/> class.</summary>
    /// <param name="message">The message to carry.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public SlskdSearchRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// slskd's search endpoints. A search is created, polled, read once and then deleted; nothing here
/// decides <em>what</em> to search for or how often.
/// </summary>
public interface ISlskdSearchApi
{
    /// <summary>Creates a search (<c>POST /api/v0/searches</c>).</summary>
    /// <exception cref="SlskdSearchRejectedException">slskd answered 429.</exception>
    Task<SlskdSearch> StartAsync(SlskdSearchRequest request, CancellationToken cancellationToken);

    /// <summary>Reads a search's state (<c>GET /api/v0/searches/{id}</c>); <c>null</c> when slskd no longer has it.</summary>
    Task<SlskdSearch?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Reads a search's responses (<c>GET /api/v0/searches/{id}/responses</c>); empty when slskd no longer has the search.</summary>
    Task<IReadOnlyList<SlskdSearchResponse>> GetResponsesAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stops a search early (<c>PUT /api/v0/searches/{id}</c>); a search slskd no longer has is ignored.</summary>
    Task StopAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes a search (<c>DELETE /api/v0/searches/{id}</c>); a search slskd no longer has is ignored.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SlskdSearchApi : ISlskdSearchApi
{
    /// <summary>Path of slskd's search collection (slskd's own API version, not Wondarr's).</summary>
    public const string SearchesPath = "/api/v0/searches";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ISlskdEndpoint _endpoint;

    /// <summary>Initialises a new instance of the <see cref="SlskdSearchApi"/> class.</summary>
    /// <param name="http">The typed client, whose base address is slskd's loopback API.</param>
    /// <param name="endpoint">Where slskd is and how Wondarr authenticates to it.</param>
    public SlskdSearchApi(HttpClient http, ISlskdEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);

        _http = http;
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    public async Task<SlskdSearch> StartAsync(SlskdSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (baseAddress, apiKey) = await _endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, SearchesPath))
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };
        message.Headers.Add(SlskdClient.ApiKeyHeader, apiKey);

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SlskdSearchRejectedException();
        }

        response.EnsureSuccessStatusCode();

        var search = await response.Content
            .ReadFromJsonAsync<SlskdSearch>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return search ?? throw new HttpRequestException("slskd accepted the search but returned no state for it");
    }

    /// <inheritdoc />
    public async Task<SlskdSearch?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, SearchPath(id), null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<SlskdSearch>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdSearchResponse>> GetResponsesAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{SearchPath(id)}/responses", null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var responses = await response.Content
            .ReadFromJsonAsync<List<SlskdSearchResponse>>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return responses ?? [];
    }

    /// <inheritdoc />
    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, SearchPath(id), null, cancellationToken)
            .ConfigureAwait(false);

        // Stopping a search that has already been deleted is the same outcome as stopping it.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, SearchPath(id), null, cancellationToken)
            .ConfigureAwait(false);

        // 204 when it was there, 404 when someone got to it first: both mean it is gone.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private static string SearchPath(Guid id) => $"{SearchesPath}/{id:D}";

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var (baseAddress, apiKey) = await _endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Not disposed here: the caller reads the response's content, which the request's content
        // would take with it (SendAsync is only called without content today).
        var message = new HttpRequestMessage(method, new Uri(baseAddress, path)) { Content = content };
        message.Headers.Add(SlskdClient.ApiKeyHeader, apiKey);

        return await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}
