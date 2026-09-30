using Wondarr.Core.Domain;
using Wondarr.Core.References;

namespace Wondarr.Api.References;

/// <summary>
/// A reference library as the API and the UI read it (LIBRARY_OUTPUT §7.6). The mode is the wire name
/// (<c>reference</c> or <c>adopt</c>) rather than the enum's number, like every other enum this API
/// sends.
/// </summary>
/// <param name="Id">The library id.</param>
/// <param name="Name">The name the user gave it; unique, case-insensitively.</param>
/// <param name="RootPath">The absolute path of the folder to walk.</param>
/// <param name="Mode"><c>reference</c> (read only) or <c>adopt</c>.</param>
/// <param name="LibraryId">The managed library adopted songs are filed under, or <see langword="null"/>.</param>
/// <param name="Enabled">Whether the daily scan covers this library.</param>
/// <param name="LastScannedAt">The UTC instant of the last completed scan, or <see langword="null"/>.</param>
/// <param name="LastScanMessage">The one-line summary of the last scan, or why it did not run.</param>
/// <param name="Counts">How many of the library's files are in each state.</param>
public sealed record ReferenceLibraryResource(
    long Id,
    string Name,
    string RootPath,
    string Mode,
    long? LibraryId,
    bool Enabled,
    DateTime? LastScannedAt,
    string? LastScanMessage,
    ReferenceLibraryCountsResource Counts);

/// <summary>How many of a reference library's files are in each state.</summary>
/// <param name="Total">Every file row of the library.</param>
/// <param name="Pending">Scanned, not yet identified.</param>
/// <param name="Identified">Identified as a song.</param>
/// <param name="Ambiguous">Ranked candidates the user has to choose between.</param>
/// <param name="Unmatched">Nothing was found.</param>
/// <param name="Adopted">Moved into a managed library.</param>
/// <param name="Unreadable">Not decodable audio.</param>
/// <param name="Missing">Gone from disk.</param>
/// <param name="Skipped">The user asked Wondarr to leave the file alone.</param>
public sealed record ReferenceLibraryCountsResource(
    int Total,
    int Pending,
    int Identified,
    int Ambiguous,
    int Unmatched,
    int Adopted,
    int Unreadable,
    int Missing,
    int Skipped);

/// <summary>What a caller sends when adding or replacing a reference library.</summary>
/// <param name="Name">The name the user gave it; unique, case-insensitively.</param>
/// <param name="RootPath">The absolute path of the folder to walk.</param>
/// <param name="Mode"><c>reference</c> (read only) or <c>adopt</c>.</param>
/// <param name="LibraryId">The managed library adopted songs are filed under, or <see langword="null"/>.</param>
/// <param name="Enabled">Whether the daily scan covers this library.</param>
public sealed record ReferenceLibraryInputResource(
    string Name,
    string RootPath,
    string Mode,
    long? LibraryId,
    bool Enabled);

/// <summary>Maps reference libraries onto the wire shapes, in both directions.</summary>
public static class ReferenceLibraryResourceMapper
{
    /// <summary>The wire name of <see cref="ReferenceLibraryMode.Reference"/>.</summary>
    public const string ReferenceMode = "reference";

    /// <summary>The wire name of <see cref="ReferenceLibraryMode.Adopt"/>.</summary>
    public const string AdoptMode = "adopt";

    /// <summary>Builds the resource for a stored library and its counts.</summary>
    /// <param name="summary">The library and how many of its files are in each state.</param>
    /// <returns>The wire resource.</returns>
    public static ReferenceLibraryResource ToResource(this ReferenceLibrarySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var library = summary.Library;
        var counts = summary.Counts;

        return new ReferenceLibraryResource(
            library.Id,
            library.Name,
            library.RootPath,
            WireName(library.Mode),
            library.LibraryId,
            library.Enabled,
            library.LastScannedAt,
            library.LastScanMessage,
            new ReferenceLibraryCountsResource(
                counts.Total,
                counts.Pending,
                counts.Identified,
                counts.Ambiguous,
                counts.Unmatched,
                counts.Adopted,
                counts.Unreadable,
                counts.Missing,
                counts.Skipped));
    }

    /// <summary>The mode a request body names, or <see langword="null"/> when it names no mode this API knows.</summary>
    /// <param name="mode">The wire name, in any case.</param>
    /// <returns>The mode, or <see langword="null"/>.</returns>
    public static ReferenceLibraryMode? ToMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        ReferenceMode => ReferenceLibraryMode.Reference,
        AdoptMode => ReferenceLibraryMode.Adopt,
        _ => null,
    };

    /// <summary>Reads the resource as the service's input, once the mode is known to be valid.</summary>
    /// <param name="resource">The request body.</param>
    /// <param name="mode">The parsed mode.</param>
    /// <returns>The service input.</returns>
    public static ReferenceLibraryInput ToInput(this ReferenceLibraryInputResource resource, ReferenceLibraryMode mode)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new ReferenceLibraryInput(resource.Name, resource.RootPath, mode, resource.LibraryId, resource.Enabled);
    }

    private static string WireName(ReferenceLibraryMode mode) =>
        mode == ReferenceLibraryMode.Adopt ? AdoptMode : ReferenceMode;
}
