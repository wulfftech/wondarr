using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// Where songs are filed: a root folder, a layout preset, the naming template, the album policy and
/// the sidecars written alongside. Stored in the <c>library</c> table.
/// </summary>
public sealed class Library : EntityBase
{
    /// <summary>Gets or sets the library name, for example "Music".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the root folder every path in this library is built under.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the layout preset the naming template and defaults came from.</summary>
    public LibraryLayout Layout { get; set; } = LibraryLayout.Plexamp;

    /// <summary>Gets or sets the Lidarr-style path template, for example <c>{Album Artist Name}/{Album Title}/{track:00} - {Track Title}</c>.</summary>
    public string NamingTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets the sidecar options as JSON text, defaulting to an empty object.</summary>
    public string SidecarOptions { get; set; } = "{}";

    /// <summary>Gets or sets how songs are assigned to album folders.</summary>
    public AlbumPolicy AlbumPolicy { get; set; } = AlbumPolicy.FewestAlbums;

    /// <summary>Gets or sets how many owned tracks a real album must hold before the policy uses it instead of the pseudo-album.</summary>
    public int MinTracksPerRealAlbum { get; set; } = 2;

    /// <summary>Gets or sets the Plex library section id, or <see langword="null"/> while not linked.</summary>
    public string? PlexSectionId { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the library new songs go to by default.</summary>
    public bool IsDefault { get; set; }
}
