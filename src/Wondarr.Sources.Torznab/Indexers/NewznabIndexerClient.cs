using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The Newznab indexer client: the original wire dialect, whose answers carry the
/// <c>newznab:attr</c> elements, serving usenet.
/// </summary>
public sealed class NewznabIndexerClient : NewznabClientBase
{
    /// <summary>Initialises a new instance of the <see cref="NewznabIndexerClient"/> class.</summary>
    /// <param name="clients">Where the named indexer client comes from.</param>
    /// <param name="capabilities">The caps reader that decides which search the indexer supports.</param>
    /// <param name="secrets">Where the API key is registered before the first request.</param>
    /// <param name="logger">The log sink.</param>
    public NewznabIndexerClient(
        IHttpClientFactory clients,
        NewznabCapabilitiesReader capabilities,
        ISecretRegistry secrets,
        ILogger<NewznabIndexerClient> logger)
        : base(clients, capabilities, secrets, logger, new NewznabRssParser(secrets, logger))
    {
    }

    /// <inheritdoc />
    protected override DownloadProtocol Protocol => DownloadProtocol.Usenet;
}
