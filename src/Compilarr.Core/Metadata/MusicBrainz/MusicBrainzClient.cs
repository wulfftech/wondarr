using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Compilarr.Core.Metadata.MusicBrainz;

/// <summary>
/// The MusicBrainz WS/2 client. Every response is cached under the provider
/// <see cref="Provider"/>, keyed by the relative request URI, so a repeat lookup makes no request at
/// all; a 404 is cached the same way, as a negative entry.
/// </summary>
public sealed class MusicBrainzClient : IMusicBrainzClient
{
    /// <summary>The provider key used in the metadata cache.</summary>
    public const string Provider = "musicbrainz";

    /// <summary>What a cached "MusicBrainz does not know this" entry stores.</summary>
    private const string NotFoundPayload = "null";

    /// <summary>How long a search page stays fresh.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromDays(1);

    /// <summary>How long a recording, ISRC or browse page stays fresh.</summary>
    private static readonly TimeSpan LookupTtl = TimeSpan.FromDays(7);

    /// <summary>How long a release (with its tracklist) stays fresh.</summary>
    private static readonly TimeSpan ReleaseTtl = TimeSpan.FromDays(30);

    /// <summary>How long a 404 stays a 404.</summary>
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(1);

    /// <summary>Two letters, three alphanumerics and seven digits.</summary>
    private static readonly Regex IsrcPattern = new("^[A-Z]{2}[A-Z0-9]{3}\\d{7}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
    };

    private readonly HttpClient _http;
    private readonly IMetadataCache _cache;

    /// <summary>Initialises a new instance of the <see cref="MusicBrainzClient"/> class.</summary>
    public MusicBrainzClient(HttpClient http, IMetadataCache cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);

        _http = http;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<MbRecordingSearchResult> SearchRecordingsAsync(
        string luceneQuery,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luceneQuery);

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "MusicBrainz serves between 1 and 100 search results per request.");
        }

        var uri = $"recording?query={Uri.EscapeDataString(luceneQuery)}"
            + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&fmt=json";

        var body = await GetBodyAsync(uri, SearchTtl, cancellationToken).ConfigureAwait(false);

        return body is null
            ? new MbRecordingSearchResult()
            : JsonSerializer.Deserialize<MbRecordingSearchResult>(body, SerializerOptions) ?? new MbRecordingSearchResult();
    }

    /// <inheritdoc />
    public async Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeMbid(recordingId, nameof(recordingId));

        var body = await GetBodyAsync($"recording/{id}?inc=artist-credits+isrcs&fmt=json", LookupTtl, cancellationToken)
            .ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<MbRecording>(body, SerializerOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MbRecording>> GetRecordingsByIsrcAsync(
        string isrc,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeIsrc(isrc);

        var body = await GetBodyAsync($"isrc/{normalized}?inc=artist-credits&fmt=json", LookupTtl, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<MbIsrcResult>(body, SerializerOptions)?.Recordings ?? [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MbRelease>> GetReleasesForRecordingAsync(
        string recordingId,
        int maxPages = 3,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeMbid(recordingId, nameof(recordingId));

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages), maxPages, "At least one page must be requested.");
        }

        var releases = new List<MbRelease>();
        var offset = 0;

        for (var page = 0; page < maxPages; page++)
        {
            var uri = $"release?recording={id}&status=official&inc=release-groups+media+artist-credits"
                + $"&limit=100&offset={offset.ToString(CultureInfo.InvariantCulture)}&fmt=json";

            var body = await GetBodyAsync(uri, LookupTtl, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                // MusicBrainz does not know the recording at all.
                break;
            }

            var parsed = JsonSerializer.Deserialize<MbReleaseBrowsePage>(body, SerializerOptions);
            if (parsed is null || parsed.Releases.Count == 0)
            {
                break;
            }

            releases.AddRange(parsed.Releases);
            offset += parsed.Releases.Count;

            if (offset >= parsed.ReleaseCount)
            {
                break;
            }
        }

        return releases;
    }

    /// <inheritdoc />
    public async Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeMbid(releaseId, nameof(releaseId));

        var body = await GetBodyAsync(
                $"release/{id}?inc=recordings+artist-credits+release-groups&fmt=json",
                ReleaseTtl,
                cancellationToken)
            .ConfigureAwait(false);

        return body is null ? null : JsonSerializer.Deserialize<MbRelease>(body, SerializerOptions);
    }

    /// <summary>
    /// Serves a request from the cache, or makes it and caches what comes back.
    /// </summary>
    /// <returns>The body, or <see langword="null"/> for a cached or fresh 404.</returns>
    private async Task<string?> GetBodyAsync(string relativeUri, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(Provider, relativeUri, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached == NotFoundPayload ? null : cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await _cache
                .SetAsync(Provider, relativeUri, NotFoundPayload, NotFoundTtl, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Value-free on purpose: response headers can carry a rate-limit bucket identifier.
            throw new MetadataProviderException(
                Provider,
                response.StatusCode,
                $"MusicBrainz answered {(int)response.StatusCode} for a {request.Method} request.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        await _cache.SetAsync(Provider, relativeUri, body, ttl, cancellationToken).ConfigureAwait(false);

        return body;
    }

    /// <summary>
    /// Validates an MBID and lower-cases it. MusicBrainz answers 400 to a malformed id and returns
    /// lower-case ids, so both happen before the request.
    /// </summary>
    private static string NormalizeMbid(string id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var guid) || guid == Guid.Empty)
        {
            throw new ArgumentException(
                $"'{parameterName}' must be a MusicBrainz identifier: a non-empty GUID.",
                parameterName);
        }

        return guid.ToString("D");
    }

    /// <summary>Validates and upper-cases an ISRC.</summary>
    private static string NormalizeIsrc(string isrc)
    {
        var normalized = (isrc ?? string.Empty).Trim().ToUpperInvariant();

        if (!IsrcPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"'{nameof(isrc)}' must be a 12-character ISRC: two letters, three alphanumerics and seven digits.",
                nameof(isrc));
        }

        return normalized;
    }
}
