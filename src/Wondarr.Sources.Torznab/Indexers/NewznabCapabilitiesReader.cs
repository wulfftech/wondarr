// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Newznab/NewznabCapabilitiesProvider.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: an IMemoryCache entry per indexer row id and URL for 24 h instead of Lidarr's
// 7-day process cache, a failed read is not cached, the reader throws IndexerException so a
// connection test can name what failed, and music search is read under both its <music-search> and
// <audio-search> spellings.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Reads an indexer's <c>t=caps</c> answer and remembers it. <see cref="GetAsync"/> serves every
/// search from the cache and falls back to Lidarr's defaults when the answer cannot be read;
/// <see cref="ReadAsync"/> goes to the wire every time and throws, which is what a connection test
/// wants.
/// </summary>
public sealed partial class NewznabCapabilitiesReader
{
    /// <summary>How long a successful caps read is remembered.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _clients;
    private readonly IMemoryCache _cache;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<NewznabCapabilitiesReader> _logger;

    /// <summary>Initialises a new instance of the <see cref="NewznabCapabilitiesReader"/> class.</summary>
    /// <param name="clients">Where the named indexer client comes from.</param>
    /// <param name="cache">Where the caps answers are remembered for 24 hours.</param>
    /// <param name="secrets">Where the API key is registered before the first request.</param>
    /// <param name="logger">The log sink; a failed read is a warning.</param>
    public NewznabCapabilitiesReader(
        IHttpClientFactory clients,
        IMemoryCache cache,
        ISecretRegistry secrets,
        ILogger<NewznabCapabilitiesReader> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _cache = cache;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>
    /// Returns the indexer's capabilities, reading them at most once every 24 hours. A read that
    /// fails is not cached — the next search asks again — and serves Lidarr's defaults meanwhile.
    /// </summary>
    /// <param name="indexerId">The indexer row's id.</param>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<NewznabCapabilities> GetAsync(long indexerId, IndexerEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var key = CacheKey(indexerId, endpoint);

        if (_cache.TryGetValue(key, out NewznabCapabilities? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var capabilities = await ReadAsync(endpoint, cancellationToken).ConfigureAwait(false);

            _cache.Set(key, capabilities, CacheDuration);

            return capabilities;
        }
        catch (IndexerException exception)
        {
            LogCapsUnavailable(_logger, exception.Message);

            return NewznabCapabilities.Defaults;
        }
    }

    /// <summary>
    /// Reads the indexer's capabilities from the wire, without the cache, and throws
    /// <see cref="IndexerException"/> naming what failed.
    /// </summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<NewznabCapabilities> ReadAsync(IndexerEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        _secrets.Register(endpoint.ApiKey);

        var url = BuildCapsUrl(endpoint);
        var client = _clients.CreateClient(IndexerHttp.ClientName);

        using var response = await IndexerHttp.GetAsync(client, url, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw StatusError(response);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return Parse(content);
    }

    /// <summary>The caps URL: the API address with <c>t=caps</c> and the API key.</summary>
    /// <param name="endpoint">The indexer's settings.</param>
    internal static Uri BuildCapsUrl(IndexerEndpoint endpoint)
    {
        var url = endpoint.BaseUri.AbsoluteUri + "?t=caps";

        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            url += "&apikey=" + Uri.EscapeDataString(endpoint.ApiKey);
        }

        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// Parses a caps answer. The <c>&lt;searching&gt;</c> section is read the way Lidarr reads it:
    /// only an <c>available="yes"</c> search counts, and music search hides under either its
    /// <c>music-search</c> or its older <c>audio-search</c> spelling.
    /// </summary>
    /// <param name="content">The answer's body.</param>
    internal NewznabCapabilities Parse(string content)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using var reader = XmlReader.Create(new StringReader(content), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new IndexerException("The indexer's answer is not valid XML.", exception);
        }

        RssParser.CheckError(document, _secrets.Redact);

        var root = document.Element("caps") ?? throw new IndexerException("No <caps> in the answer.");

        var capabilities = new NewznabCapabilities();

        if (root.Element("limits") is { } limits)
        {
            if (int.TryParse(limits.Attribute("default")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defaultPageSize))
            {
                capabilities = capabilities with { DefaultPageSize = defaultPageSize };
            }

            if (int.TryParse(limits.Attribute("max")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPageSize))
            {
                capabilities = capabilities with { MaxPageSize = maxPageSize };
            }
        }

        if (root.Element("searching") is { } searching)
        {
            capabilities = capabilities with
            {
                SearchParams = SearchParameters(searching.Element("search")),
                MusicSearchParams = SearchParameters(searching.Element("music-search") ?? searching.Element("audio-search")),
            };
        }

        if (root.Element("categories") is { } categories)
        {
            capabilities = capabilities with { Categories = [.. categories.Elements("category").Select(ReadCategory)] };
        }

        return capabilities;
    }

    /// <summary>
    /// The parameters a search element offers, or <see langword="null"/> when the element is missing
    /// or not available.
    /// </summary>
    /// <param name="search">The <c>search</c>, <c>music-search</c> or <c>audio-search</c> element.</param>
    private static IReadOnlyList<string>? SearchParameters(XElement? search)
    {
        if (search is null || !string.Equals(search.Attribute("available")?.Value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parameters = search.Attribute("supportedParams")?.Value;

        return parameters is null
            ? []
            : [.. parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Reads one category with its subcategories.</summary>
    /// <param name="category">The <c>category</c> element.</param>
    private static NewznabCategory ReadCategory(XElement category) => new(
        int.Parse(category.Attribute("id")?.Value ?? "0", CultureInfo.InvariantCulture),
        category.Attribute("name")?.Value ?? string.Empty,
        category.Attribute("description")?.Value ?? string.Empty,
        [.. category.Elements("subcat").Select(subcat => new NewznabCategory(
            int.Parse(subcat.Attribute("id")?.Value ?? "0", CultureInfo.InvariantCulture),
            subcat.Attribute("name")?.Value ?? string.Empty,
            subcat.Attribute("description")?.Value ?? string.Empty,
            []))]);

    /// <summary>The message for an answer whose status is not a success.</summary>
    /// <param name="response">The answer.</param>
    private static IndexerException StatusError(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;

        return code is 401 or 403
            ? new IndexerException($"The indexer answered {code}: check the API key")
            : new IndexerException($"The indexer answered {code}.");
    }

    private static string CacheKey(long indexerId, IndexerEndpoint endpoint) =>
        $"indexer-caps:{indexerId}:{endpoint.BaseUri.AbsoluteUri}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the indexer's caps; using the defaults: {Message}")]
    private static partial void LogCapsUnavailable(ILogger logger, string message);
}
