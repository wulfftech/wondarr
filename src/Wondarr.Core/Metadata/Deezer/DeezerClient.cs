using System.Globalization;
using System.Text.Json;

namespace Wondarr.Core.Metadata.Deezer;

/// <summary>
/// Reads tracks and albums from the Deezer API, unauthenticated. Every response is cached, so a
/// repeat lookup costs nothing — except <see cref="GetFreshPreviewUrlAsync"/>, whose answer is
/// signed and expires within the half hour.
/// </summary>
public interface IDeezerClient
{
    /// <summary>
    /// Searches for tracks. The query is plain text: Deezer's <c>artist:"…" track:"…"</c> syntax
    /// answers with no results at all, so callers filter what comes back.
    /// </summary>
    /// <param name="query">Plain text, usually "artist title".</param>
    /// <param name="limit">How many results to ask for; Deezer serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<DeezerSearchResult> SearchTracksAsync(
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default);

    /// <summary>Looks a track up by its Deezer id.</summary>
    /// <param name="id">The Deezer track id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The track, or <see langword="null"/> when Deezer does not know the id.</returns>
    Task<DeezerTrack?> GetTrackAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks a track up by ISRC. The ISRC is not unique upstream — several releases carry it — so
    /// Deezer answers with whichever one it likes.
    /// </summary>
    /// <param name="isrc">The 12-character ISRC, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The track, or <see langword="null"/> when Deezer does not know the ISRC.</returns>
    Task<DeezerTrack?> GetTrackByIsrcAsync(string isrc, CancellationToken cancellationToken = default);

    /// <summary>Looks an album up by its Deezer id.</summary>
    /// <param name="id">The Deezer album id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The album, or <see langword="null"/> when Deezer does not know the id.</returns>
    Task<DeezerAlbum?> GetAlbumAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Searches for albums. The query is plain text, like <see cref="SearchTracksAsync"/>.</summary>
    /// <param name="query">Plain text, usually "artist album".</param>
    /// <param name="limit">How many results to ask for; Deezer serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<DeezerAlbumSearchResult> SearchAlbumsAsync(
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of a playlist's tracks, in playlist order. Deezer answers "no data" for an
    /// unknown or private playlist.
    /// </summary>
    /// <param name="playlistId">The Deezer playlist id.</param>
    /// <param name="index">The page's first track (0, 100, 200, …).</param>
    /// <param name="limit">How many tracks to ask for; Deezer serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page, or <see langword="null"/> when the playlist is unknown or private.</returns>
    Task<DeezerTrackPage?> GetPlaylistTracksAsync(
        long playlistId,
        int index,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an artist's top tracks, most played first. These rows carry no ISRC.</summary>
    /// <param name="artistId">The Deezer artist id.</param>
    /// <param name="limit">How many tracks to ask for; Deezer serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page, or <see langword="null"/> when Deezer does not know the artist.</returns>
    Task<DeezerTrackPage?> GetArtistTopAsync(long artistId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Searches for artists. The query is plain text, like <see cref="SearchTracksAsync"/>.</summary>
    /// <param name="name">The artist's name, as the user typed it.</param>
    /// <param name="limit">How many results to ask for; Deezer serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<DeezerArtistSearchResult> SearchArtistsAsync(
        string name,
        int limit = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a track's preview URL <em>without</em> consulting the cache. Preview URLs are signed and
    /// expire after about half an hour, so a cached one is worthless by the time it is played.
    /// </summary>
    /// <param name="trackId">The Deezer track id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The preview URL, or <see langword="null"/> when the track has none or is unknown.</returns>
    Task<string?> GetFreshPreviewUrlAsync(long trackId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Deezer client. Deezer reports errors — including "no data" — as <em>HTTP 200</em> with an
/// <c>error</c> object, so every body is inspected before it is believed; the quota error (code 4) is
/// turned into a 429 by <see cref="DeezerQuotaHandler"/> so the retry pipeline can act on it.
/// </summary>
public sealed class DeezerClient : IDeezerClient
{
    /// <summary>The provider key used in the metadata cache.</summary>
    public const string Provider = "deezer";

    /// <summary>Deezer's "no data" error code, which means the id or ISRC is unknown.</summary>
    public const int NoDataCode = 800;

    /// <summary>Deezer's "Quota limit exceeded" error code.</summary>
    public const int QuotaExceededCode = 4;

    /// <summary>What a cached "Deezer does not know this" entry stores.</summary>
    private const string NoDataPayload = "null";

    /// <summary>How long a search page stays fresh.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromDays(1);

    /// <summary>How long a track stays fresh.</summary>
    private static readonly TimeSpan TrackTtl = TimeSpan.FromDays(7);

    /// <summary>How long an album stays fresh.</summary>
    private static readonly TimeSpan AlbumTtl = TimeSpan.FromDays(30);

    /// <summary>How long a playlist's track page stays fresh: a playlist changes, and a sync an hour later must see the change.</summary>
    private static readonly TimeSpan PlaylistTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long an artist's top tracks stay fresh.</summary>
    private static readonly TimeSpan ArtistTopTtl = TimeSpan.FromDays(1);

    /// <summary>How long a "no data" answer stays unanswered.</summary>
    private static readonly TimeSpan NoDataTtl = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly IMetadataCache _cache;

    /// <summary>Initialises a new instance of the <see cref="DeezerClient"/> class.</summary>
    /// <param name="http">The typed client.</param>
    /// <param name="cache">The shared provider-response cache.</param>
    public DeezerClient(HttpClient http, IMetadataCache cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);

        _http = http;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<DeezerSearchResult> SearchTracksAsync(
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Deezer serves between 1 and 100 search results per request.");
        }

        var uri = $"search?q={Uri.EscapeDataString(query)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";

        var body = await GetBodyAsync(uri, SearchTtl, cancellationToken).ConfigureAwait(false);

        return body is null
            ? new DeezerSearchResult()
            : JsonSerializer.Deserialize<DeezerSearchResult>(body, SerializerOptions) ?? new DeezerSearchResult();
    }

    /// <inheritdoc />
    public Task<DeezerTrack?> GetTrackAsync(long id, CancellationToken cancellationToken = default) =>
        GetTrackAsync($"track/{NormalizeId(id, nameof(id))}", TrackTtl, cancellationToken);

    /// <inheritdoc />
    public Task<DeezerTrack?> GetTrackByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isrc);

        var normalized = isrc.Trim().ToUpperInvariant();

        return GetTrackAsync($"track/isrc:{normalized}", TrackTtl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeezerAlbum?> GetAlbumAsync(long id, CancellationToken cancellationToken = default)
    {
        var uri = $"album/{NormalizeId(id, nameof(id))}";

        var body = await GetBodyAsync(uri, AlbumTtl, cancellationToken).ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<DeezerAlbum>(body, SerializerOptions);
    }

    /// <inheritdoc />
    public async Task<DeezerAlbumSearchResult> SearchAlbumsAsync(
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Deezer serves between 1 and 100 search results per request.");
        }

        var uri = $"search/album?q={Uri.EscapeDataString(query)}"
            + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}";

        var body = await GetBodyAsync(uri, SearchTtl, cancellationToken).ConfigureAwait(false);

        return body is null
            ? new DeezerAlbumSearchResult()
            : JsonSerializer.Deserialize<DeezerAlbumSearchResult>(body, SerializerOptions)
                ?? new DeezerAlbumSearchResult();
    }

    /// <inheritdoc />
    public async Task<DeezerTrackPage?> GetPlaylistTracksAsync(
        long playlistId,
        int index,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "A page starts at 0.");
        }

        var uri = $"playlist/{NormalizeId(playlistId, nameof(playlistId))}/tracks"
            + $"?index={index.ToString(CultureInfo.InvariantCulture)}"
            + $"&limit={Limit(limit, nameof(limit))}";

        var body = await GetBodyAsync(uri, PlaylistTtl, cancellationToken).ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<DeezerTrackPage>(body, SerializerOptions) ?? new DeezerTrackPage();
    }

    /// <inheritdoc />
    public async Task<DeezerTrackPage?> GetArtistTopAsync(
        long artistId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var uri = $"artist/{NormalizeId(artistId, nameof(artistId))}/top?limit={Limit(limit, nameof(limit))}";

        var body = await GetBodyAsync(uri, ArtistTopTtl, cancellationToken).ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<DeezerTrackPage>(body, SerializerOptions) ?? new DeezerTrackPage();
    }

    /// <inheritdoc />
    public async Task<DeezerArtistSearchResult> SearchArtistsAsync(
        string name,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var uri = $"search/artist?q={Uri.EscapeDataString(name)}&limit={Limit(limit, nameof(limit))}";

        var body = await GetBodyAsync(uri, SearchTtl, cancellationToken).ConfigureAwait(false);

        return body is null
            ? new DeezerArtistSearchResult()
            : JsonSerializer.Deserialize<DeezerArtistSearchResult>(body, SerializerOptions) ?? new DeezerArtistSearchResult();
    }

    /// <inheritdoc />
    public async Task<string?> GetFreshPreviewUrlAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var uri = $"track/{NormalizeId(trackId, nameof(trackId))}";

        // Deliberately not GetBodyAsync: the cached body carries a preview URL that has expired by
        // now, and caching this answer would spread the dead URL to everyone else.
        var body = await SendAsync(uri, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            return null;
        }

        var track = JsonSerializer.Deserialize<DeezerTrack>(body, SerializerOptions);

        return string.IsNullOrWhiteSpace(track?.Preview) ? null : track!.Preview;
    }

    /// <summary>Serves a track request from the cache, or makes it.</summary>
    private async Task<DeezerTrack?> GetTrackAsync(string uri, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var body = await GetBodyAsync(uri, ttl, cancellationToken).ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<DeezerTrack>(body, SerializerOptions);
    }

    /// <summary>
    /// Serves a request from the cache, or makes it, caches the body — or the "no data" answer — and
    /// returns it.
    /// </summary>
    /// <returns>The body, or <see langword="null"/> for a cached or fresh "no data" answer.</returns>
    private async Task<string?> GetBodyAsync(string relativeUri, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(Provider, relativeUri, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached == NoDataPayload ? null : cached;
        }

        var body = await SendAsync(relativeUri, cancellationToken).ConfigureAwait(false);

        await _cache
            .SetAsync(Provider, relativeUri, body ?? NoDataPayload, body is null ? NoDataTtl : ttl, cancellationToken)
            .ConfigureAwait(false);

        return body;
    }

    /// <summary>
    /// Makes one request and unwraps Deezer's convention of reporting errors inside a 200.
    /// </summary>
    /// <returns>The body, or <see langword="null"/> when Deezer answered "no data".</returns>
    /// <exception cref="MetadataProviderException">
    /// The request failed, or Deezer answered with an error code this client cannot recover from.
    /// </exception>
    private async Task<string?> SendAsync(string relativeUri, CancellationToken cancellationToken)
    {
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
                $"Deezer answered {(int)response.StatusCode} for a {request.Method} request.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!TryReadErrorCode(body, out var code))
        {
            return body;
        }

        if (code == NoDataCode)
        {
            return null;
        }

        // Code 4 never reaches here in production: DeezerQuotaHandler has already turned it into a
        // 429 and the retry pipeline has already spent its attempts on it.
        throw new MetadataProviderException(
            Provider,
            response.StatusCode,
            $"Deezer answered HTTP {(int)response.StatusCode} with error code {code.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Reads the error code out of a body, when it carries an <c>error</c> object.</summary>
    private static bool TryReadErrorCode(string? body, out int code)
    {
        code = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out var value)
                || !value.TryGetInt32(out code))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            // Not JSON at all: a proxy answering with HTML, say. Let the caller's parser deal with it.
            return false;
        }
    }

    /// <summary>Rejects the ids Deezer would answer with a "no data" error anyway.</summary>
    private static long NormalizeId(long id, string parameterName)
    {
        if (id < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, id, "A Deezer id is a positive integer.");
        }

        return id;
    }

    /// <summary>Checks the page size Deezer serves (1 to 100) and returns it.</summary>
    private static int Limit(int limit, string parameterName) =>
        limit is < 1 or > 100
            ? throw new ArgumentOutOfRangeException(parameterName, limit, "Deezer serves between 1 and 100 items per request.")
            : limit;
}
