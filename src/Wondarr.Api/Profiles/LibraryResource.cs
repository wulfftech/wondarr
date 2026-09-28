using System.Text.Json;
using Wondarr.Core.Domain;

namespace Wondarr.Api.Profiles;

/// <summary>
/// A library as the Settings UI expects it. The enums travel as camelCase strings (<c>plexamp</c>,
/// <c>fewestAlbums</c>, <c>artistAlbum</c>), and <c>sidecarOptions</c> is the JSON object the
/// <c>library.sidecar_options</c> text column holds.
/// </summary>
/// <param name="Id">The <c>library</c> row id.</param>
/// <param name="Name">The library name.</param>
/// <param name="RootPath">The root folder every path in this library is built under.</param>
/// <param name="Layout">The layout preset.</param>
/// <param name="NamingTemplate">The Lidarr-style path template.</param>
/// <param name="SidecarOptions">The sidecar options as a JSON object.</param>
/// <param name="AlbumPolicy">How songs are assigned to album folders.</param>
/// <param name="MinTracksPerRealAlbum">How many owned tracks a real album needs before the policy uses it.</param>
/// <param name="PlexSectionId">The Plex section id, or <see langword="null"/> while not linked.</param>
/// <param name="IsDefault">Whether new songs go here by default.</param>
public sealed record LibraryResource(
    long Id,
    string Name,
    string RootPath,
    LibraryLayout Layout,
    string NamingTemplate,
    JsonElement SidecarOptions,
    AlbumPolicy AlbumPolicy,
    int MinTracksPerRealAlbum,
    string? PlexSectionId,
    bool IsDefault);

/// <summary>Maps between <see cref="Library"/> and <see cref="LibraryResource"/>.</summary>
public static class LibraryResourceMapper
{
    /// <summary>The sidecar options a library without any stores.</summary>
    private const string EmptyOptions = "{}";

    /// <summary>Builds the resource for <paramref name="library"/>.</summary>
    /// <param name="library">The stored library.</param>
    public static LibraryResource ToResource(this Library library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return new LibraryResource(
            library.Id,
            library.Name,
            library.RootPath,
            library.Layout,
            library.NamingTemplate,
            ParseOptions(library.SidecarOptions),
            library.AlbumPolicy,
            library.MinTracksPerRealAlbum,
            library.PlexSectionId,
            library.IsDefault);
    }

    /// <summary>Builds the library <paramref name="resource"/> describes.</summary>
    /// <param name="resource">The resource to read.</param>
    /// <param name="id">The id to store it under: the route id.</param>
    public static Library ToLibrary(this LibraryResource resource, long id)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new Library
        {
            Id = id,
            Name = resource.Name,
            RootPath = resource.RootPath,
            Layout = resource.Layout,
            NamingTemplate = resource.NamingTemplate,
            SidecarOptions = resource.SidecarOptions.ValueKind == JsonValueKind.Undefined
                ? EmptyOptions
                : resource.SidecarOptions.GetRawText(),
            AlbumPolicy = resource.AlbumPolicy,
            MinTracksPerRealAlbum = resource.MinTracksPerRealAlbum,
            PlexSectionId = resource.PlexSectionId,
            IsDefault = resource.IsDefault,
        };
    }

    /// <summary>
    /// Parses the stored JSON text. A value the column cannot hold would have been rejected on write,
    /// so a value that no longer parses degrades to an empty object rather than failing the read.
    /// </summary>
    private static JsonElement ParseOptions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? EmptyOptions : json);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var fallback = JsonDocument.Parse(EmptyOptions);

            return fallback.RootElement.Clone();
        }
    }
}
