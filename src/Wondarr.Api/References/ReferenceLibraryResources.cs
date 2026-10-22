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
/// <param name="ReplaceBelowCutoff">
/// Whether a song held by one of the library's files may be replaced by a better copy while the file is
/// below the song's cutoff. The file itself is never touched.
/// </param>
public sealed record ReferenceLibraryResource(
    long Id,
    string Name,
    string RootPath,
    string Mode,
    long? LibraryId,
    bool Enabled,
    DateTime? LastScannedAt,
    string? LastScanMessage,
    ReferenceLibraryCountsResource Counts,
    bool ReplaceBelowCutoff);

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
/// <param name="LikelyYouTubeRips">Files still on disk that are most likely YouTube rips (lossy at 48 kHz).</param>
/// <param name="Replaced">Identified files whose song now holds a library file, so they can be deleted.</param>
public sealed record ReferenceLibraryCountsResource(
    int Total,
    int Pending,
    int Identified,
    int Ambiguous,
    int Unmatched,
    int Adopted,
    int Unreadable,
    int Missing,
    int Skipped,
    int LikelyYouTubeRips,
    int Replaced);

/// <summary>What a caller sends when adding or replacing a reference library.</summary>
/// <param name="Name">The name the user gave it; unique, case-insensitively.</param>
/// <param name="RootPath">The absolute path of the folder to walk.</param>
/// <param name="Mode"><c>reference</c> (read only) or <c>adopt</c>.</param>
/// <param name="LibraryId">The managed library adopted songs are filed under, or <see langword="null"/>.</param>
/// <param name="Enabled">Whether the daily scan covers this library.</param>
/// <param name="ReplaceBelowCutoff">
/// Whether a song held by one of the library's files may be replaced by a better copy while the file is
/// below the song's cutoff; <see langword="false"/> when the body leaves it out.
/// </param>
public sealed record ReferenceLibraryInputResource(
    string Name,
    string RootPath,
    string Mode,
    long? LibraryId,
    bool Enabled,
    bool ReplaceBelowCutoff = false);

/// <summary>
/// A reference file a library copy has replaced: its song now holds a file in the managed library, so
/// nothing uses this one and the user may delete it. Wondarr never does.
/// </summary>
/// <param name="Id">The reference file id.</param>
/// <param name="ReferenceLibraryId">The library the file belongs to.</param>
/// <param name="ReferenceLibraryName">That library's name.</param>
/// <param name="RelativePath">The path relative to the library root, separated by <c>/</c>.</param>
/// <param name="Path">The file's absolute path, as the container sees it.</param>
/// <param name="LikelyYouTubeRip">Whether the file is most likely a YouTube rip.</param>
/// <param name="Quality">The quality the file is rated at, or <see langword="null"/> when it was never measured.</param>
/// <param name="SongId">The song.</param>
/// <param name="ArtistCredit">The song's artist credit.</param>
/// <param name="Title">The song's title.</param>
/// <param name="LibraryPath">The library file the song holds now.</param>
/// <param name="LibraryQuality">That file's quality.</param>
/// <param name="LibraryFileSince">When that file was imported, in UTC.</param>
/// <param name="LibraryCopyRatesLower">Whether the library file rates below this one, so this is the better file to keep.</param>
public sealed record ReplacedReferenceFileResource(
    long Id,
    long ReferenceLibraryId,
    string ReferenceLibraryName,
    string RelativePath,
    string Path,
    bool LikelyYouTubeRip,
    string? Quality,
    long SongId,
    string ArtistCredit,
    string Title,
    string LibraryPath,
    string LibraryQuality,
    DateTime LibraryFileSince,
    bool LibraryCopyRatesLower);

/// <summary>How many songs deleting a reference library would affect.</summary>
/// <param name="Count">The songs owned through one of the library's files.</param>
public sealed record ReferenceLibraryOwnedSongsResource(int Count);

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
                counts.Skipped,
                counts.LikelyYouTubeRips,
                counts.Replaced),
            library.ReplaceBelowCutoff);
    }

    /// <summary>Builds the resource for one replaced reference file.</summary>
    /// <param name="file">The replaced file.</param>
    /// <returns>The wire resource.</returns>
    public static ReplacedReferenceFileResource ToResource(this ReplacedReferenceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new ReplacedReferenceFileResource(
            file.Id,
            file.ReferenceLibraryId,
            file.ReferenceLibraryName,
            file.RelativePath,
            file.Path,
            file.LikelyYouTubeRip,
            file.QualityName,
            file.SongId,
            file.ArtistCredit,
            file.Title,
            file.LibraryPath,
            file.LibraryQualityName,
            file.LibraryFileSince,
            file.LibraryCopyRatesLower);
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

    /// <summary>The delete choice a request names, or <see langword="null"/> when it names none this API knows.</summary>
    /// <param name="choice">The wire name (<c>search</c>, <c>wanted</c> or <c>unmonitor</c>), in any case.</param>
    /// <returns>The choice, or <see langword="null"/>.</returns>
    public static ReferenceLibraryDeleteChoice? ToDeleteChoice(string? choice) => choice?.Trim().ToLowerInvariant() switch
    {
        "search" => ReferenceLibraryDeleteChoice.Search,
        "wanted" => ReferenceLibraryDeleteChoice.Wanted,
        "unmonitor" => ReferenceLibraryDeleteChoice.Unmonitor,
        _ => null,
    };

    /// <summary>Reads the resource as the service's input, once the mode is known to be valid.</summary>
    /// <param name="resource">The request body.</param>
    /// <param name="mode">The parsed mode.</param>
    /// <returns>The service input.</returns>
    public static ReferenceLibraryInput ToInput(this ReferenceLibraryInputResource resource, ReferenceLibraryMode mode)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new ReferenceLibraryInput(
            resource.Name,
            resource.RootPath,
            mode,
            resource.LibraryId,
            resource.Enabled,
            resource.ReplaceBelowCutoff);
    }

    private static string WireName(ReferenceLibraryMode mode) =>
        mode == ReferenceLibraryMode.Adopt ? AdoptMode : ReferenceMode;
}
