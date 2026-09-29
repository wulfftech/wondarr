using System.Text.Json;
using Wondarr.Core.Domain;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The typed form of a library's <c>SidecarOptions</c> JSON: which sidecars the layout writes next to
/// the audio file, and how large an embedded cover may be (LIBRARY_OUTPUT.md §7.4).
/// </summary>
/// <remarks>
/// Reading is deliberately forgiving — this is user-editable JSON behind a settings page that does not
/// exist yet (P3-12a) — so an unknown key, a missing key or malformed JSON all fall back to the
/// defaults rather than failing an import.
/// </remarks>
public sealed record LibrarySidecarOptions
{
    /// <summary>The smallest cover edge a library may ask for.</summary>
    public const int MinCoverMaxEdge = 300;

    /// <summary>The largest cover edge a library may ask for.</summary>
    public const int MaxCoverMaxEdge = 4000;

    /// <summary>The default cover edge, in pixels.</summary>
    public const int DefaultCoverMaxEdge = 1400;

    private static readonly JsonSerializerOptions Serializer = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Gets a value indicating whether an album folder gets a <c>cover.jpg</c>.</summary>
    public bool CoverJpg { get; init; } = true;

    /// <summary>Gets the longest edge an embedded cover may have, in pixels.</summary>
    public int CoverMaxEdge { get; init; } = DefaultCoverMaxEdge;

    /// <summary>Gets a value indicating whether a lyrics sidecar is written. Read by P3-08.</summary>
    public bool Lyrics { get; init; } = true;

    /// <summary>
    /// Parses a library's sidecar options. Unknown properties are ignored, missing ones keep their
    /// defaults, and malformed JSON yields the defaults; the cover edge is clamped to
    /// <see cref="MinCoverMaxEdge"/>–<see cref="MaxCoverMaxEdge"/>.
    /// </summary>
    /// <param name="json">The library's <c>SidecarOptions</c> text, possibly null or empty.</param>
    /// <returns>The parsed options.</returns>
    public static LibrarySidecarOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LibrarySidecarOptions();
        }

        LibrarySidecarOptions parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<LibrarySidecarOptions>(json, Serializer)
                ?? new LibrarySidecarOptions();
        }
        catch (JsonException)
        {
            return new LibrarySidecarOptions();
        }

        return parsed with
        {
            CoverMaxEdge = Math.Clamp(parsed.CoverMaxEdge, MinCoverMaxEdge, MaxCoverMaxEdge),
        };
    }

    /// <summary>Writes the options back as the camelCase JSON the library column holds.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Serializer);

    /// <summary>
    /// Whether a layout files songs into album folders at all. Only those layouts get folder art: a
    /// flat or artist-only layout has no per-album folder to put it in.
    /// </summary>
    /// <param name="layout">The library's layout preset.</param>
    /// <returns>Whether the layout has an album layer.</returns>
    public static bool HasAlbumFolder(LibraryLayout layout) =>
        layout is LibraryLayout.Plexamp or LibraryLayout.ArtistAlbum;
}