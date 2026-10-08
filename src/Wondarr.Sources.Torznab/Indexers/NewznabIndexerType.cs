using System.Text.Json;
using Wondarr.Core.Indexers;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The Newznab indexer type: a usenet source that speaks the original Newznab API — a Newznab site,
/// or Prowlarr's or NZBHydra's per-indexer endpoint for one.
/// </summary>
public sealed class NewznabIndexerType : IIndexerType
{
    private readonly NewznabCapabilitiesReader _capabilities;

    /// <summary>Initialises a new instance of the <see cref="NewznabIndexerType"/> class.</summary>
    /// <param name="capabilities">The caps reader a connection test goes through.</param>
    public NewznabIndexerType(NewznabCapabilitiesReader capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        _capabilities = capabilities;
    }

    /// <inheritdoc />
    public string Type => "newznab";

    /// <inheritdoc />
    public DownloadProtocol? Protocol => DownloadProtocol.Usenet;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "text", Required: true, HelpText: "The indexer's Newznab endpoint."),
        new("apiPath", "API path", "text", Required: false, Advanced: true, HelpText: "The path the Newznab API lives under; /api unless the indexer says otherwise."),
        new("apiKey", "API key", "password", Required: false, Secret: true, HelpText: "The key the indexer expects in the apikey parameter."),
        new("categories", "Categories", "text", Required: false, HelpText: "The newznab category ids to search, comma-separated; 3000 is music."),
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
