using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Compilarr.Core.Metadata.ITunes;

/// <summary>One song from the iTunes Search API.</summary>
public sealed record ITunesTrack
{
    /// <summary>Gets the iTunes track id.</summary>
    public long TrackId { get; init; }

    /// <summary>Gets the track name.</summary>
    public string TrackName { get; init; } = string.Empty;

    /// <summary>Gets the performing artist's name.</summary>
    public string ArtistName { get; init; } = string.Empty;

    /// <summary>Gets the collection (album) name.</summary>
    public string? CollectionName { get; init; }

    /// <summary>Gets the duration in milliseconds.</summary>
    public int? TrackTimeMillis { get; init; }

    /// <summary>Gets the 30-second preview URL.</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>Gets the artwork URL, which is documented at 100×100 only.</summary>
    public string? ArtworkUrl100 { get; init; }

    /// <summary>Gets the release date, as iTunes writes it (<c>yyyy-MM-ddTHH:mm:ssZ</c>).</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the track number within its disc.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Gets the disc number.</summary>
    public int? DiscNumber { get; init; }
}

/// <summary>The search or lookup response envelope: <c>{ resultCount, results }</c>.</summary>
public sealed record ITunesSearchResult
{
    /// <summary>Gets the number of results returned.</summary>
    public int ResultCount { get; init; }

    /// <summary>Gets the results.</summary>
    public IReadOnlyList<ITunesTrack> Results { get; init; } = [];
}

/// <summary>
/// Reads songs and artwork from the iTunes Search API. It is unauthenticated but tightly
/// rate-limited, so everything is cached and the caller's gate is deliberately slow.
/// </summary>
public interface IITunesClient
{
    /// <summary>Searches for songs.</summary>
    /// <param name="term">Plain text, usually "artist title".</param>
    /// <param name="limit">How many results to ask for.</param>
    /// <param name="country">The storefront, as an ISO-3166 code.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<ITunesTrack>> SearchSongsAsync(
        string term,
        int limit = 10,
        string country = "US",
        CancellationToken cancellationToken = default);

    /// <summary>Looks a track up by its iTunes id.</summary>
    /// <param name="id">The iTunes track id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The track, or <see langword="null"/> when iTunes knows nothing by that id.</returns>
    Task<ITunesTrack?> LookupAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The iTunes client. iTunes answers <c>403</c> when the caller goes over ~20 requests a minute;
/// that is a rate limit, not a transient fault, so it is never retried and surfaces as a
/// <see cref="MetadataProviderException"/> the cover-art chain treats as a miss.
/// </summary>
public sealed class ITunesClient : IITunesClient
{
    /// <summary>The provider key used in the metadata cache.</summary>
    public const string Provider = "itunes";

    /// <summary>The artwork size token the API documents.</summary>
    private const string DocumentedArtworkToken = "100x100bb";

    /// <summary>How long a search page or a lookup stays fresh.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IMetadataCache _cache;

    /// <summary>Initialises a new instance of the <see cref="ITunesClient"/> class.</summary>
    /// <param name="http">The typed client.</param>
    /// <param name="cache">The shared provider-response cache.</param>
    public ITunesClient(HttpClient http, IMetadataCache cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);

        _http = http;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ITunesTrack>> SearchSongsAsync(
        string term,
        int limit = 10,
        string country = "US",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentException.ThrowIfNullOrWhiteSpace(country);

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "At least one result must be asked for.");
        }

        var uri = $"search?term={Uri.EscapeDataString(term)}&entity=song"
            + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
            + $"&country={Uri.EscapeDataString(country)}";

        return (await GetResultAsync(uri, cancellationToken).ConfigureAwait(false))?.Results ?? [];
    }

    /// <inheritdoc />
    public async Task<ITunesTrack?> LookupAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "An iTunes track id is a positive integer.");
        }

        var result = await GetResultAsync(
                $"lookup?id={id.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken)
            .ConfigureAwait(false);

        return result?.Results.Count > 0 ? result.Results[0] : null;
    }

    /// <summary>
    /// Rewrites documented 100-pixel artwork into the size actually wanted. The <c>{n}x{n}bb</c>
    /// token is the modern bounding-box form; the legacy <c>100000x100000-999</c> one now answers 400.
    /// </summary>
    /// <param name="artworkUrl100">The <c>artworkUrl100</c> value from a result.</param>
    /// <param name="size">The square size to ask for, in pixels.</param>
    /// <returns>The rewritten URL, or <see langword="null"/> when there was no URL to rewrite.</returns>
    public static string? ToArtworkUrl(string? artworkUrl100, int size = 600)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl100))
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        return artworkUrl100.Replace(
            DocumentedArtworkToken,
            $"{size.ToString(CultureInfo.InvariantCulture)}x{size.ToString(CultureInfo.InvariantCulture)}bb",
            StringComparison.Ordinal);
    }

    /// <summary>Serves a request from the cache, or makes it and caches the body.</summary>
    private async Task<ITunesSearchResult?> GetResultAsync(string relativeUri, CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(Provider, relativeUri, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<ITunesSearchResult>(cached, SerializerOptions);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Value-free on purpose: response headers can carry a rate-limit bucket identifier.
            throw new MetadataProviderException(
                Provider,
                response.StatusCode,
                $"iTunes answered {(int)response.StatusCode} for a {request.Method} request.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        await _cache.SetAsync(Provider, relativeUri, body, Ttl, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<ITunesSearchResult>(body, SerializerOptions);
    }
}
