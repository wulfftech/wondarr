using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// An artist credited on at least one song. Stored in the <c>artist</c> table.
/// </summary>
public sealed class Artist : EntityBase
{
    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the name used for sorting, for example "Daft Punk" or "Beatles, The".</summary>
    public string SortName { get; set; } = string.Empty;

    /// <summary>Gets or sets the lower-case MusicBrainz artist id, or <see langword="null"/>.</summary>
    public string? MbArtistId { get; set; }

    /// <summary>Gets or sets the Spotify artist id, or <see langword="null"/>.</summary>
    public string? SpotifyId { get; set; }

    /// <summary>Gets or sets the Deezer artist id, or <see langword="null"/>.</summary>
    public long? DeezerId { get; set; }

    /// <summary>Gets or sets how many of the artist's top songs are monitored, or <see langword="null"/> for all.</summary>
    public int? MonitoredTopN { get; set; }

    /// <summary>Gets or sets free-form tags, stored as a JSON array.</summary>
    public List<string> Tags { get; set; } = [];
}
