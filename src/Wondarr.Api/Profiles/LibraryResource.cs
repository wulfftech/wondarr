using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Profiles;

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
/// <param name="OutputPolicy">
/// The output policy as a JSON object, version 2: one rule per source class — <c>youtube</c>,
/// <c>lossy</c>, <c>lossless</c> — each naming a codec (<c>keep</c>, <c>aac</c>, <c>mp3</c>,
/// <c>opus</c>, <c>flac</c>, <c>alac</c>), a mode, a bitrate and a sample rate (ADR-0008). A
/// library without one reports the default (YouTube downloads become AAC 256 kbps CBR in an
/// <c>.m4a</c>, everything else is kept as it was served). A <c>PUT</c> may still send a
/// version-1 object — the flat, YouTube-only shape — which is stored and read back as version 2.
/// </param>
/// <param name="AlbumPolicy">How songs are assigned to album folders.</param>
/// <param name="MinTracksPerRealAlbum">How many owned tracks a real album needs before the policy uses it.</param>
/// <param name="PlexSectionId">The Plex section id, or <see langword="null"/> while not linked.</param>
/// <param name="PlexLibraryPath">
/// The root folder as the Plex server sees it, or <see langword="null"/> when the server sees the
/// same path Wondarr writes to.
/// </param>
/// <param name="IsDefault">Whether new songs go here by default.</param>
/// <param name="ReplayGain">
/// Whether every file placed in this library carries ReplayGain track gain and peak tags. Off by
/// default; the audio is never changed. On a PUT an omitted key keeps the stored value; on a POST it means off.
/// </param>
public sealed record LibraryResource(
    long Id,
    string Name,
    string RootPath,
    LibraryLayout Layout,
    string NamingTemplate,
    JsonElement SidecarOptions,
    JsonElement OutputPolicy,
    AlbumPolicy AlbumPolicy,
    int MinTracksPerRealAlbum,
    string? PlexSectionId,
    string? PlexLibraryPath,
    bool IsDefault,
    bool? ReplayGain = null);

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
            ParseOptions(string.IsNullOrWhiteSpace(library.OutputPolicy)
                ? LibraryOutputPolicy.Default.ToJson()
                : library.OutputPolicy),
            library.AlbumPolicy,
            library.MinTracksPerRealAlbum,
            library.PlexSectionId,
            library.PlexLibraryPath,
            library.IsDefault,
            library.ReplayGain);
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
            // An explicit null clears the policy back to the default (a PUT replaces the library,
            // so "no policy" must be expressible); an omitted key keeps whatever the row held.
            OutputPolicy = resource.OutputPolicy.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : resource.OutputPolicy.GetRawText(),
            AlbumPolicy = resource.AlbumPolicy,
            MinTracksPerRealAlbum = resource.MinTracksPerRealAlbum,
            PlexSectionId = resource.PlexSectionId,
            PlexLibraryPath = resource.PlexLibraryPath,
            IsDefault = resource.IsDefault,
            ReplayGain = resource.ReplayGain ?? false,
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
