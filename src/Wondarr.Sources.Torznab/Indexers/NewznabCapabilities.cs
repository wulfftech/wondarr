// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Newznab/NewznabCapabilities.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: TV search, the aggregate-id flag and the search-engine names are dropped
// (Wondarr searches music only), and the defaults leave music search unavailable so that a failed
// caps read falls back to a plain q search instead of promising the indexer more than it offers.

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>One newznab category, with the subcategories the caps answer listed under it.</summary>
/// <param name="Id">The category's newznab id, for example 3000.</param>
/// <param name="Name">The category's name, for example <c>Music</c>.</param>
/// <param name="Description">The category's description, or empty.</param>
/// <param name="Subcategories">The subcategories listed under this category.</param>
public sealed record NewznabCategory(int Id, string Name, string Description, IReadOnlyList<NewznabCategory> Subcategories);

/// <summary>
/// What a Torznab or Newznab indexer's <c>t=caps</c> answer says it can do: which searches it offers
/// and with which parameters, how many results it returns, and the categories it knows.
/// </summary>
public sealed record NewznabCapabilities
{
    /// <summary>
    /// Lidarr's defaults, used when the caps answer cannot be read: the plain <c>q</c> search only,
    /// music search unavailable, and a page of at most 100 results.
    /// </summary>
    public static NewznabCapabilities Defaults { get; } = new();

    /// <summary>Gets how many results the indexer returns when the caller does not ask for more.</summary>
    public int DefaultPageSize { get; init; } = 100;

    /// <summary>Gets the most results the indexer will return for one request.</summary>
    public int MaxPageSize { get; init; } = 100;

    /// <summary>
    /// Gets the parameters the plain text search supports, or <see langword="null"/> when the
    /// indexer offers no text search.
    /// </summary>
    public IReadOnlyList<string>? SearchParams { get; init; } = ["q"];

    /// <summary>
    /// Gets the parameters the music search supports, or <see langword="null"/> when the indexer
    /// offers no music search.
    /// </summary>
    public IReadOnlyList<string>? MusicSearchParams { get; init; }

    /// <summary>Gets the categories the indexer listed, with their subcategories.</summary>
    public IReadOnlyList<NewznabCategory> Categories { get; init; } = [];

    /// <summary>Gets whether the music search can be given both an artist and an album.</summary>
    public bool SupportsMusicSearch =>
        MusicSearchParams is { } parameters &&
        parameters.Contains("artist", StringComparer.Ordinal) &&
        parameters.Contains("album", StringComparer.Ordinal);

    /// <summary>Gets whether the plain text search can be given a query.</summary>
    public bool SupportsSearch =>
        SearchParams is { } parameters && parameters.Contains("q", StringComparer.Ordinal);
}
