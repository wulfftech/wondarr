using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The "indexer" of a release pushed to <c>/api/v1/release/push</c> (DECISIONS build session 8 #11):
/// there is no indexer row and nothing to search, only the pushed link to download. The grab path
/// reaches it through <see cref="IIndexerClientFactory"/> like any other indexer, with
/// <see cref="Row"/>.
/// </summary>
public sealed partial class PushedReleaseClient : IIndexerClient
{
    /// <summary>The type name of the pseudo-indexer a pushed release is grabbed through.</summary>
    public const string Type = "push";

    /// <summary>The indexer id a pushed release carries: no row has it.</summary>
    public const long IndexerId = 0;

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<PushedReleaseClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="PushedReleaseClient"/> class.</summary>
    /// <param name="clients">Creates the indexer HTTP client the download goes through.</param>
    /// <param name="logger">Logs the download's path, never its query (it can carry a passkey).</param>
    public PushedReleaseClient(IHttpClientFactory clients, ILogger<PushedReleaseClient> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _logger = logger;
    }

    /// <summary>The pseudo-indexer row of a pushed release.</summary>
    /// <param name="name">The indexer name the push gave, when it gave one.</param>
    /// <param name="protocol">The release's protocol.</param>
    public static Indexer Row(string? name, DownloadProtocol protocol) => new()
    {
        Id = IndexerId,
        Name = string.IsNullOrWhiteSpace(name) ? "Release push" : name,
        Type = Type,
        Protocol = protocol,
        Enabled = true,
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexerRelease>>([]);

    /// <inheritdoc />
    public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        var target = release.DownloadUrl ?? release.MagnetUrl
            ?? throw new IndexerException("The pushed release has no URL to download.");

        if (target.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new IndexerDownload(null, target);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new IndexerException("The pushed release's download URL is not an http(s) address.");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var path = IndexerHttp.WithoutQuery(url);
            LogDownloading(_logger, path);
        }

        using var response = await IndexerHttp.GetAsync(_clients.CreateClient(IndexerHttp.ClientName), url, cancellationToken)
            .ConfigureAwait(false);

        if (IndexerHttp.TryGetMagnetRedirect(response, out var magnet))
        {
            return new IndexerDownload(null, magnet);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"The pushed release's link answered {(int)response.StatusCode}.");
        }

        return new IndexerDownload(await IndexerHttp.ReadCappedAsync(response, cancellationToken).ConfigureAwait(false), null);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Downloading a pushed release from {Path}")]
    private static partial void LogDownloading(ILogger logger, Uri path);
}
