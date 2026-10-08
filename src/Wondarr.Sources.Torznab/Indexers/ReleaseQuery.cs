namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// What an indexer is asked for: the artist and the album of the release that contains the wanted
/// song, and the release year when it is known.
/// </summary>
/// <param name="Artist">The artist's name.</param>
/// <param name="Album">The album's title.</param>
/// <param name="Year">The album's year, or <see langword="null"/> when it is unknown.</param>
public sealed record ReleaseQuery(string Artist, string Album, int? Year);
