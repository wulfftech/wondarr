using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.ImportLists.References;

/// <summary>
/// The reference-library import list (ADR-0012): a reference library's identified files, in folder
/// order, so a DJ set or a curated folder becomes a playlist. The provider only reads; the sync
/// resolves each row by its recording MBID and finds the song the file is already identified as, so
/// the item is "already in the library" — the point of this list is its order and its playlist.
/// </summary>
/// <remarks>
/// Registered <b>scoped</b>, unlike the other providers: it reads the scoped
/// <see cref="WondarrDbContext"/>. <see cref="Validate"/> cannot reach the database, so it only
/// checks the shape of the id; whether the library exists is answered by <see cref="FetchAsync"/>.
/// </remarks>
public sealed class ReferenceLibraryListProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string ReferenceLibraryType = "referenceLibrary";

    private static readonly NotificationField[] SettingsFields =
    [
        new("referenceLibraryId", "Reference library", "number", true, "The reference library to read"),
    ];

    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryListProvider"/> class.</summary>
    /// <param name="database">The scoped database context.</param>
    public ReferenceLibraryListProvider(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public string Type => ReferenceLibraryType;

    /// <inheritdoc />
    public string DisplayName => "Reference library";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) =>
        Id(settings) is { } id && id > 0
            ? []
            : ["Choose a reference library to read."];

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        long id;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            id = Id(settings.RootElement) ?? 0;
        }

        if (id < 1)
        {
            return ImportListFetchResult.Failed("The list has no reference library to read.");
        }

        if (await _database.ReferenceLibraries
                .AsNoTracking()
                .SingleOrDefaultAsync(library => library.Id == id, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return ImportListFetchResult.Failed(
                string.Create(CultureInfo.InvariantCulture, $"Reference library {id.ToString(CultureInfo.InvariantCulture)} does not exist."));
        }

        // Only the files identification settled go on the list: a pending, ambiguous or unmatched
        // file is not a song yet, and the list's order is the folder's order.
        var rows = await _database.ReferenceFiles
            .AsNoTracking()
            .Where(file => file.ReferenceLibraryId == id && file.SongId != null)
            .Join(
                _database.Songs.AsNoTracking(),
                file => file.SongId,
                song => song.Id,
                (file, song) => new Row(file.Id, file.RelativePath, song.ArtistCredit, song.Title, song.MbRecordingId, song.DurationMs))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = rows
            .OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(row => new ImportListEntry(
                string.Create(CultureInfo.InvariantCulture, $"ref:{row.Id.ToString(CultureInfo.InvariantCulture)}"),
                string.IsNullOrWhiteSpace(row.ArtistCredit) ? null : row.ArtistCredit,
                string.IsNullOrWhiteSpace(row.Title) ? null : row.Title,
                DurationMs: row.DurationMs,
                MbRecordingId: row.MbRecordingId))
            .ToList();

        return new ImportListFetchResult(entries);
    }

    /// <summary>The library id of the settings, as a JSON number or a string of digits.</summary>
    private static long? Id(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("referenceLibraryId", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>One identified file and the song it is, as the read joins them.</summary>
    /// <param name="Id">The reference file's id, which the entry carries as its external id.</param>
    /// <param name="RelativePath">The file's path relative to the library root.</param>
    /// <param name="ArtistCredit">The song's artist credit.</param>
    /// <param name="Title">The song's title.</param>
    /// <param name="MbRecordingId">The song's recording MBID.</param>
    /// <param name="DurationMs">The song's length in milliseconds.</param>
    private sealed record Row(
        long Id,
        string RelativePath,
        string ArtistCredit,
        string Title,
        string? MbRecordingId,
        int? DurationMs);
}
