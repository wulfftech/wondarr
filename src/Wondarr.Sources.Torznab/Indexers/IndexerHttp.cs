using System.Net;
using System.Reflection;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The HTTP behaviour shared by the Torznab and Newznab clients: one named <see cref="HttpClient"/>,
/// redirects followed by hand so a download URL that points at a <c>magnet:</c> URI can be handed
/// back instead of followed (Prowlarr's proxy links do this), and a download cap.
/// </summary>
public static class IndexerHttp
{
    /// <summary>The name of the named <see cref="HttpClient"/> every indexer request goes through.</summary>
    public const string ClientName = "indexer";

    /// <summary>How long one request may take before it is abandoned.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most bytes one download may return; a bigger answer is refused.</summary>
    public const long MaxDownloadBytes = 10 * 1024 * 1024;

    /// <summary>The most redirects one request follows.</summary>
    public const int MaxRedirects = 10;

    /// <summary>Gets the User-Agent every request carries.</summary>
    public static string UserAgent { get; } = BuildUserAgent();

    /// <summary>
    /// The URL without its query, the only form that is ever logged: the API key lives in the query.
    /// </summary>
    /// <param name="url">The URL a request goes to.</param>
    public static Uri WithoutQuery(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return new Uri(url.GetLeftPart(UriPartial.Path), UriKind.Absolute);
    }

    /// <summary>
    /// Sends one GET, following redirects itself (the named client has auto-redirect off). A redirect
    /// whose target is a <c>magnet:</c> URI is not followed: the answer is returned as it stands so
    /// the caller can read the magnet out of it.
    /// </summary>
    /// <param name="client">The client to send through.</param>
    /// <param name="url">The URL to ask for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The final answer, or the redirect whose target is a magnet.</returns>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);

        for (var hop = 0; ; hop++)
        {
            if (hop > MaxRedirects)
            {
                throw new IndexerException("The indexer redirected more times than Wondarr follows.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            HttpResponseMessage response;

            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The transport's own message names the host, so only the fact is carried across.
                throw new IndexerException("The indexer could not be reached.", exception);
            }

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return response;
            }

            if (!Uri.TryCreate(url, response.Headers.Location, out var next))
            {
                response.Dispose();
                throw new IndexerException("The indexer sent a redirect Wondarr cannot follow.");
            }

            if (next.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }

            url = next;
            response.Dispose();
        }
    }

    /// <summary>
    /// Whether an answer is a redirect to a <c>magnet:</c> URI, and the magnet it points at.
    /// </summary>
    /// <param name="response">The answer <see cref="GetAsync"/> returned.</param>
    /// <param name="magnet">The magnet the answer redirects to.</param>
    public static bool TryGetMagnetRedirect(HttpResponseMessage response, out string? magnet)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (IsRedirect(response.StatusCode) &&
            response.Headers.Location is { } location &&
            location.IsAbsoluteUri &&
            location.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            magnet = location.AbsoluteUri;

            return true;
        }

        magnet = null;

        return false;
    }

    /// <summary>
    /// Reads an answer's body, refusing anything over <see cref="MaxDownloadBytes"/> however the
    /// length is announced.
    /// </summary>
    /// <param name="response">The answer to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body's bytes.</returns>
    public static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw TooBig();
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();

        var buffer = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > MaxDownloadBytes)
            {
                throw TooBig();
            }

            body.Write(buffer, 0, read);
        }
    }

    /// <summary>Whether a status is one Wondarr follows by hand.</summary>
    /// <param name="statusCode">The answer's status.</param>
    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static IndexerException TooBig() =>
        new($"The download is bigger than the {MaxDownloadBytes / (1024 * 1024)} MiB Wondarr accepts from an indexer.");

    private static string BuildUserAgent()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        // Informational versions carry the source revision after a '+'; that is build noise here.
        var plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus >= 0 ? "Wondarr/" + version[..plus] : "Wondarr/" + version;
    }
}
