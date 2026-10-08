using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// What a grab brought back: the downloaded container (a <c>.torrent</c> or an <c>.nzb</c>), or a
/// magnet link when the download URL redirected to one, the way Prowlarr's proxy links do.
/// </summary>
/// <param name="Content">The downloaded bytes, or <see langword="null"/> when <paramref name="MagnetUrl"/> is set.</param>
/// <param name="MagnetUrl">The magnet the download URL redirected to, or <see langword="null"/> when bytes were downloaded.</param>
public sealed record IndexerDownload(byte[]? Content, string? MagnetUrl);

/// <summary>
/// Talks to one indexer row: searches it for a release and downloads one of the releases it listed.
/// One implementation exists per indexer type.
/// </summary>
public interface IIndexerClient
{
    /// <summary>Searches the indexer for the releases that match the query.</summary>
    /// <param name="indexer">The indexer row, with its settings.</param>
    /// <param name="query">What to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken);

    /// <summary>Downloads one release the indexer listed.</summary>
    /// <param name="indexer">The indexer row, with its settings.</param>
    /// <param name="release">The release to download.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken);
}

/// <summary>
/// Chooses the <see cref="IIndexerClient"/> for an indexer row by its
/// <see cref="Wondarr.Core.Domain.Indexer.Type"/> name. A factory rather than a keyed DI registration,
/// because the key is a database value rather than a compile-time one, and the caller (the search
/// runner, P7-04) holds a row, not a type name.
/// </summary>
public interface IIndexerClientFactory
{
    /// <summary>Returns the client that serves the indexer row's type.</summary>
    /// <param name="indexer">The row whose type names the client.</param>
    IIndexerClient GetClient(Indexer indexer);
}
