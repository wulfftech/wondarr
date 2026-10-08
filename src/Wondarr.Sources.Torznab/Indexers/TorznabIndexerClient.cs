using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The Torznab indexer client: a Newznab wire dialect whose answers carry the
/// <c>torznab:attr</c> elements, serving torrents.
/// </summary>
public sealed class TorznabIndexerClient : NewznabClientBase
{
    /// <summary>Initialises a new instance of the <see cref="TorznabIndexerClient"/> class.</summary>
    /// <param name="clients">Where the named indexer client comes from.</param>
    /// <param name="capabilities">The caps reader that decides which search the indexer supports.</param>
    /// <param name="secrets">Where the API key is registered before the first request.</param>
    /// <param name="logger">The log sink.</param>
    public TorznabIndexerClient(
        IHttpClientFactory clients,
        NewznabCapabilitiesReader capabilities,
        ISecretRegistry secrets,
        ILogger<TorznabIndexerClient> logger)
        : base(clients, capabilities, secrets, logger, new TorznabRssParser(secrets, logger))
    {
    }

    /// <inheritdoc />
    protected override DownloadProtocol Protocol => DownloadProtocol.Torrent;
}
