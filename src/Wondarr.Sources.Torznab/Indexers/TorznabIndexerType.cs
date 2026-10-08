using System.Text.Json;
using Wondarr.Core.Indexers;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The Torznab indexer type: a torrent source that speaks the Torznab dialect of the Newznab API —
/// Prowlarr's per-indexer endpoint, Jackett, NZBHydra or a tracker's own Torznab feed.
/// </summary>
public sealed class TorznabIndexerType : IIndexerType
{
    private readonly NewznabCapabilitiesReader _capabilities;

    /// <summary>Initialises a new instance of the <see cref="TorznabIndexerType"/> class.</summary>
    /// <param name="capabilities">The caps reader a connection test goes through.</param>
    public TorznabIndexerType(NewznabCapabilitiesReader capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        _capabilities = capabilities;
    }

    /// <inheritdoc />
    public string Type => "torznab";

    /// <inheritdoc />
    public DownloadProtocol? Protocol => DownloadProtocol.Torrent;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "text", Required: true, HelpText: "The indexer's Torznab endpoint, for example Prowlarr's or Jackett's per-indexer URL."),
        new("apiPath", "API path", "text", Required: false, Advanced: true, HelpText: "The path the Torznab API lives under; /api unless the indexer says otherwise."),
        new("apiKey", "API key", "password", Required: false, Secret: true, HelpText: "The key the indexer expects in the apikey parameter."),
        new("categories", "Categories", "text", Required: false, HelpText: "The newznab category ids to search, comma-separated; 3000 is music."),
        new("minimumSeeders", "Minimum seeders", "number", Required: false, HelpText: "Releases with fewer seeders than this are skipped."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings) => IndexerEndpoint.Validate(settings);

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var endpoint = IndexerEndpoint.Read(settings);

        try
        {
            await _capabilities.ReadAsync(endpoint, cancellationToken).ConfigureAwait(false);

            return new ProviderTestResult(true, null);
        }
        catch (IndexerException exception)
        {
            return new ProviderTestResult(false, exception.Message);
        }
    }
}
