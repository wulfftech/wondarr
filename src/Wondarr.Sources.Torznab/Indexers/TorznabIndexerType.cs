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
    private readonly TorznabIndexerClient _client;

    /// <summary>Initialises a new instance of the <see cref="TorznabIndexerType"/> class.</summary>
    /// <param name="capabilities">The caps reader a connection test goes through.</param>
    /// <param name="client">The client whose search a connection test sends after the caps read.</param>
    public TorznabIndexerType(NewznabCapabilitiesReader capabilities, TorznabIndexerClient client)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(client);

        _capabilities = capabilities;
        _client = client;
    }

    /// <inheritdoc />
    public string Type => "torznab";

    /// <inheritdoc />
    public DownloadProtocol? Protocol => DownloadProtocol.Torrent;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "text", Required: true, HelpText: "The indexer's Torznab endpoint, for example Prowlarr's or Jackett's per-indexer URL.", Target: true),
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
            // Some indexers answer caps without looking at the key and refuse every search, so the
            // caps read alone proves nothing: one real search must succeed too.
            var capabilities = await _capabilities.ReadAsync(endpoint, cancellationToken).ConfigureAwait(false);

            await _client.TestSearchAsync(endpoint, capabilities, cancellationToken).ConfigureAwait(false);

            return new ProviderTestResult(true, null);
        }
        catch (IndexerException exception)
        {
            return new ProviderTestResult(false, exception.Message);
        }
    }
}
