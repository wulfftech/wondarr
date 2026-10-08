using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Picks the client an indexer row's type names. The type names are compared without case, the way
/// <c>Wondarr.Core.Indexers.IndexerService</c> compares them.
/// </summary>
public sealed class IndexerClientFactory : IIndexerClientFactory
{
    private readonly TorznabIndexerClient _torznab;
    private readonly NewznabIndexerClient _newznab;
    private readonly PushedReleaseClient _pushed;
    private readonly ProwlarrSearchClient _prowlarr;
    private readonly GazelleClient _gazelle;

    /// <summary>Initialises a new instance of the <see cref="IndexerClientFactory"/> class.</summary>
    /// <param name="torznab">The Torznab client.</param>
    /// <param name="newznab">The Newznab client.</param>
    /// <param name="pushed">The download-only client of a pushed release's pseudo-indexer.</param>
    /// <param name="prowlarr">The client of a Prowlarr row (its <c>/api/v1/search</c>).</param>
    /// <param name="gazelle">The client of a Gazelle tracker.</param>
    public IndexerClientFactory(
        TorznabIndexerClient torznab,
        NewznabIndexerClient newznab,
        PushedReleaseClient pushed,
        ProwlarrSearchClient prowlarr,
        GazelleClient gazelle)
    {
        ArgumentNullException.ThrowIfNull(torznab);
        ArgumentNullException.ThrowIfNull(newznab);
        ArgumentNullException.ThrowIfNull(pushed);
        ArgumentNullException.ThrowIfNull(prowlarr);
        ArgumentNullException.ThrowIfNull(gazelle);

        _torznab = torznab;
        _newznab = newznab;
        _pushed = pushed;
        _prowlarr = prowlarr;
        _gazelle = gazelle;
    }

    /// <inheritdoc />
    public IIndexerClient GetClient(Indexer indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);

        return indexer.Type.ToLowerInvariant() switch
        {
            "torznab" => _torznab,
            "newznab" => _newznab,
            PushedReleaseClient.Type => _pushed,
            "prowlarr" => _prowlarr,
            "gazelle" => _gazelle,
            _ => throw new IndexerException($"There is no client for the indexer type '{indexer.Type}'."),
        };
    }
}
