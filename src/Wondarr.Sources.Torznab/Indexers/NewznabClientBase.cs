using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The search and download flow the Torznab and Newznab clients share: read the row's settings,
/// register the API key so the log pipeline can redact it, ask the caps reader what the indexer
/// supports, build the query, and parse the answer with the dialect's parser. The dialect subclasses
/// supply the protocol and the parser.
/// </summary>
public abstract partial class NewznabClientBase : IIndexerClient
{
    private readonly IHttpClientFactory _clients;
    private readonly NewznabCapabilitiesReader _capabilities;
    private readonly ISecretRegistry _secrets;
    private readonly RssParser _parser;
    private readonly ILogger _logger;

    /// <summary>Initialises a new instance of the <see cref="NewznabClientBase"/> class.</summary>
    /// <param name="clients">Where the named indexer client comes from.</param>
    /// <param name="capabilities">The caps reader that decides which search the indexer supports.</param>
    /// <param name="secrets">Where the API key is registered before the first request.</param>
    /// <param name="logger">The log sink; every request's URL is logged without its query.</param>
    /// <param name="parser">The dialect's RSS parser.</param>
    protected NewznabClientBase(
        IHttpClientFactory clients,
        NewznabCapabilitiesReader capabilities,
        ISecretRegistry secrets,
        ILogger logger,
        RssParser parser)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(parser);

        _clients = clients;
        _capabilities = capabilities;
        _secrets = secrets;
        _logger = logger;
        _parser = parser;
    }

    /// <summary>Gets how this indexer's releases are downloaded.</summary>
    protected abstract DownloadProtocol Protocol { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(query);

        var endpoint = IndexerEndpoint.Read(NotificationSecrets.Read(indexer.Settings));

        _secrets.Register(endpoint.ApiKey);

        var capabilities = await _capabilities
            .GetAsync(indexer.Id, endpoint, cancellationToken)
            .ConfigureAwait(false);

        var url = NewznabQuery.Build(endpoint, capabilities, query);

        var content = await SendSearchAsync(url, cancellationToken).ConfigureAwait(false);

        var releases = _parser.Parse(content, indexer, Protocol);

        // A torrent nobody seeds (fewer than the row's minimum) would never finish; Lidarr skips them
        // the same way. An indexer that reports no seeder count is given the benefit of the doubt.
        if (Protocol == DownloadProtocol.Torrent)
        {
            var minimum = endpoint.MinimumSeeders ?? IndexerEndpoint.DefaultMinimumSeeders;

            releases = [.. releases.Where(release => release.Seeders is not { } seeders || seeders >= minimum)];
        }

        LogFound(_logger, releases.Count, indexer.Name);

        return releases;
    }

    /// <summary>
    /// Sends the one small search a connection test makes after the caps read, through the same
    /// request, status check and answer check a real search goes through. Zero results is a pass; an
    /// indexer that refuses the search (a key that reads caps but may not search, say) throws an
    /// <see cref="IndexerException"/> carrying its own reason. Nothing is downloaded.
    /// </summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="capabilities">What the indexer's caps said it supports.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <exception cref="IndexerException">The indexer could not be reached or refused the search.</exception>
    public async Task TestSearchAsync(IndexerEndpoint endpoint, NewznabCapabilities capabilities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(capabilities);

        _secrets.Register(endpoint.ApiKey);

        var url = NewznabQuery.BuildTest(endpoint, capabilities);

        var content = await SendSearchAsync(url, cancellationToken).ConfigureAwait(false);

        _parser.Check(content);
    }

    /// <inheritdoc />
    public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(release);

        var endpoint = IndexerEndpoint.Read(NotificationSecrets.Read(indexer.Settings));

        _secrets.Register(endpoint.ApiKey);

        var target = release.DownloadUrl ?? release.MagnetUrl
            ?? throw new IndexerException("The release has no URL to download.");

        if (target.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new IndexerDownload(null, target);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var url))
        {
            throw new IndexerException("The release's download URL is not an absolute address.");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var path = IndexerHttp.WithoutQuery(url);
            LogDownloading(_logger, path);
        }

        var client = _clients.CreateClient(IndexerHttp.ClientName);

        using var response = await IndexerHttp.GetAsync(client, url, cancellationToken).ConfigureAwait(false);

        if (IndexerHttp.TryGetMagnetRedirect(response, out var magnet))
        {
            return new IndexerDownload(null, magnet);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"The indexer answered {(int)response.StatusCode}.", response.StatusCode);
        }

        var content = await IndexerHttp.ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);

        return new IndexerDownload(content, null);
    }

    /// <summary>
    /// Sends one search and returns the answer's body. A non-success answer throws; when its body is
    /// the indexer's own <c>&lt;error/&gt;</c> the exception carries the indexer's reason.
    /// </summary>
    /// <param name="url">The search's URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    private async Task<string> SendSearchAsync(Uri url, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var path = IndexerHttp.WithoutQuery(url);
            LogSearching(_logger, path);
        }

        var client = _clients.CreateClient(IndexerHttp.ClientName);

        using var response = await IndexerHttp.GetAsync(client, url, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await RssParser.ThrowIfErrorBodyAsync(response, _secrets.Redact, cancellationToken).ConfigureAwait(false);

            throw new IndexerException($"The indexer answered {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Searching {Url}")]
    private static partial void LogSearching(ILogger logger, Uri url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Downloading {Url}")]
    private static partial void LogDownloading(ILogger logger, Uri url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Indexer} listed {Count} releases")]
    private static partial void LogFound(ILogger logger, int count, string indexer);
}
