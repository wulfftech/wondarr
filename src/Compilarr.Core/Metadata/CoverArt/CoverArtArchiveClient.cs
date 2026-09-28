using System.Net;

namespace Compilarr.Core.Metadata.CoverArt;

/// <summary>
/// Asks the Cover Art Archive whether art exists for a release group or a release. It never
/// downloads an image: a hit is the stable <c>coverartarchive.org</c> URL, which redirects to the
/// archive.org copy when it is fetched.
/// </summary>
public interface ICoverArtArchiveClient
{
    /// <summary>Asks for the front image of a release group.</summary>
    /// <param name="releaseGroupId">The release group MBID, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The front image URL, or <see langword="null"/> when there is no front image.</returns>
    Task<string?> GetReleaseGroupFrontUrlAsync(string releaseGroupId, CancellationToken cancellationToken = default);

    /// <summary>Asks for the front image of a single release.</summary>
    /// <param name="releaseId">The release MBID, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The front image URL, or <see langword="null"/> when there is no front image.</returns>
    Task<string?> GetReleaseFrontUrlAsync(string releaseId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Cover Art Archive client. A front image answers <c>307</c> with a <c>Location</c> on
/// archive.org; the client returns the <em>stable</em> Cover Art Archive URL instead, because the
/// redirect target changes whenever the archive.org storage layout does and whenever a release is
/// merged into another. <c>404</c> means "no front image".
/// </summary>
public sealed class CoverArtArchiveClient : ICoverArtArchiveClient
{
    /// <summary>The provider key used in the metadata cache.</summary>
    public const string Provider = "coverartarchive";

    /// <summary>The relative path suffix that asks for a 500-pixel front thumbnail.</summary>
    private const string Front500 = "front-500";

    /// <summary>What a cached "art exists" entry stores.</summary>
    private const string ExistsPayload = "1";

    /// <summary>What a cached "no front image" entry stores.</summary>
    private const string MissingPayload = "0";

    /// <summary>How long a hit stays fresh.</summary>
    private static readonly TimeSpan ExistsTtl = TimeSpan.FromDays(30);

    /// <summary>How long a miss stays a miss. Shorter than a hit: art gets added over time.</summary>
    private static readonly TimeSpan MissingTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly IMetadataCache _cache;

    /// <summary>Initialises a new instance of the <see cref="CoverArtArchiveClient"/> class.</summary>
    /// <param name="http">The typed client, whose primary handler must not follow redirects.</param>
    /// <param name="cache">The shared provider-response cache.</param>
    public CoverArtArchiveClient(HttpClient http, IMetadataCache cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);

        _http = http;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<string?> GetReleaseGroupFrontUrlAsync(
        string releaseGroupId,
        CancellationToken cancellationToken = default) =>
        GetFrontUrlAsync("release-group", NormalizeMbid(releaseGroupId, nameof(releaseGroupId)), cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetReleaseFrontUrlAsync(
        string releaseId,
        CancellationToken cancellationToken = default) =>
        GetFrontUrlAsync("release", NormalizeMbid(releaseId, nameof(releaseId)), cancellationToken);

    /// <summary>
    /// Serves a "does front art exist?" answer from the cache, or asks with a <c>HEAD</c>.
    /// </summary>
    /// <param name="segment">Either <c>release-group</c> or <c>release</c>.</param>
    /// <param name="id">The normalised MBID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    private async Task<string?> GetFrontUrlAsync(string segment, string id, CancellationToken cancellationToken)
    {
        var relativePath = $"{segment}/{id}/{Front500}";

        var cached = await _cache.GetAsync(Provider, relativePath, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached == ExistsPayload ? BuildUrl(relativePath) : null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, relativePath);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await _cache
                .SetAsync(Provider, relativePath, MissingPayload, MissingTtl, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        // A redirect is the normal "art exists" answer; a 200 means a mirror served it directly.
        // Both are a hit, and neither is followed: the Location on archive.org is not stable.
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is < 300 or >= 400)
        {
            // Value-free on purpose: response headers can carry a rate-limit bucket identifier.
            throw new MetadataProviderException(
                Provider,
                response.StatusCode,
                $"Cover Art Archive answered {(int)response.StatusCode} for a {request.Method} request.");
        }

        await _cache
            .SetAsync(Provider, relativePath, ExistsPayload, ExistsTtl, cancellationToken)
            .ConfigureAwait(false);

        return BuildUrl(relativePath);
    }

    /// <summary>Turns a relative request path into the stable Cover Art Archive URL.</summary>
    private string BuildUrl(string relativePath)
    {
        var baseAddress = _http.BaseAddress
            ?? throw new InvalidOperationException("The Cover Art Archive client has no base address.");

        return new Uri(baseAddress, relativePath).ToString();
    }

    /// <summary>
    /// Validates an MBID and lower-cases it. The Cover Art Archive answers 400 to a malformed id and
    /// serves lower-case ids, so both happen before the request.
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
}
