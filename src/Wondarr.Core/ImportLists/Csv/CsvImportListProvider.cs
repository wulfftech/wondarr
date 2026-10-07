using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.Csv;

/// <summary>What a CSV file looks like to the provider: its format, its header and how it maps.</summary>
/// <param name="Format"><c>exportify</c> or <c>generic</c>.</param>
/// <param name="Headers">The header row as written.</param>
/// <param name="RowCount">How many data rows the file holds.</param>
/// <param name="Sample">The first few rows as entries.</param>
/// <param name="Problems">Why the file cannot be imported as mapped; empty when it can.</param>
public sealed record CsvPreview(
    string Format,
    IReadOnlyList<string> Headers,
    int RowCount,
    IReadOnlyList<ImportListEntry> Sample,
    IReadOnlyList<string> Problems);

/// <summary>
/// The CSV import list (ADR-0012): an Exportify export of a Spotify playlist, or any CSV the user maps
/// to title, artist, album, length, ISRC and MBID columns. The file's text is stored on the list
/// (<see cref="ImportList.SourceText"/>), so a sync re-reads the upload; uploading a newer export
/// replaces it. Exportify writes its header row in the user's UI language and has added columns over
/// time, so its files are recognised by English names first and by the <c>spotify:track:</c> URIs in
/// the first column second, then mapped by position (DECISIONS build session 7 #8).
/// </summary>
public sealed class CsvImportListProvider : IImportListProvider
{
    /// <summary>The format of a file Exportify wrote.</summary>
    public const string ExportifyFormat = "exportify";

    /// <summary>The format of any other CSV, mapped by column names.</summary>
    public const string GenericFormat = "generic";

    /// <summary>How many rows a preview shows.</summary>
    private const int SampleSize = 5;

    private const string SpotifyTrackPrefix = "spotify:track:";

    // Exportify's base columns (src/components/data/TracksBaseData.ts, 2026-10-07): Track URI,
    // Track Name, Artist URI(s), Artist Name(s), Album URI, Album Name, Album Artist URI(s),
    // Album Artist Name(s), Album Release Date, Album Image URL, Disc Number, Track Number,
    // Track Duration (ms), Track Preview URL, Explicit, Popularity, ISRC, …
    private static readonly CsvColumns ExportifyPositions = new(Title: 1, Artist: 3, Album: 5, Duration: 12, Isrc: 16, Mbid: -1, Id: 0);

    private static readonly NotificationField[] SettingsFields =
    [
        new("titleColumn", "Title column", "text", false, "The header of the track-title column. Leave the column fields empty for an Exportify export."),
        new("artistColumn", "Artist column", "text", false, "The header of the artist column."),
        new("albumColumn", "Album column", "text", false, "The header of the album column (optional)."),
        new("durationColumn", "Length column", "text", false, "The header of the length column: milliseconds, seconds or m:ss (optional)."),
        new("isrcColumn", "ISRC column", "text", false, "The header of the ISRC column (optional; the most reliable match)."),
        new("mbidColumn", "MusicBrainz recording id column", "text", false, "The header of a MusicBrainz recording id column (optional).", Advanced: true),
    ];

    private static readonly string[] SettingKeys =
        ["titleColumn", "artistColumn", "albumColumn", "durationColumn", "isrcColumn", "mbidColumn"];

    /// <inheritdoc />
    public string Type => ImportList.CsvType;

    /// <inheritdoc />
    public string DisplayName => "CSV file (Exportify or any mapped CSV)";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return ["Upload a CSV file."];
        }

        return Preview(sourceText, settings).Problems;
    }

    /// <inheritdoc />
    public Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (string.IsNullOrWhiteSpace(list.SourceText))
        {
            return Task.FromResult(ImportListFetchResult.Failed("The list has no uploaded file."));
        }

        using var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings);
        var read = Read(list.SourceText, settings.RootElement);

        return Task.FromResult(read.Problems.Count > 0
            ? ImportListFetchResult.Failed(string.Join(" ", read.Problems))
            : new ImportListFetchResult(read.Entries));
    }

    /// <summary>Reads a file as the list would: its format, its header, a few rows, and any problem.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="settings">The column mapping, or an empty object.</param>
    public static CsvPreview Preview(string text, JsonElement settings)
    {
        ArgumentNullException.ThrowIfNull(text);

        var read = Read(text, settings);

        return new CsvPreview(read.Format, read.Headers, read.Entries.Count, [.. read.Entries.Take(SampleSize)], read.Problems);
    }

    private static ReadResult Read(string text, JsonElement settings)
    {
        var rows = CsvText.Read(text);

        if (rows.Count == 0)
        {
            return new ReadResult(GenericFormat, [], [], ["The file is empty."]);
        }

        var header = rows[0].Select(cell => cell.Trim()).ToArray();
        var data = rows.Skip(1).ToList();
        var problems = new List<string>();
        var (format, columns) = Layout(header, data.Count > 0 ? data[0] : null, settings, problems);

        if (problems.Count > 0 || columns is null)
        {
            return new ReadResult(format, header, [], problems);
        }

        var entries = new List<ImportListEntry>(data.Count);

        foreach (var row in data)
        {
            if (Entry(row, columns, format) is { } entry)
            {
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            problems.Add("No row has a title, an ISRC or a MusicBrainz id.");
        }

        return new ReadResult(format, header, entries, problems);
    }

    private static (string Format, CsvColumns? Columns) Layout(
        string[] header,
        string[]? firstRow,
        JsonElement settings,
        List<string> problems)
    {
        var mapping = ReadMapping(settings);

        if (mapping.Count > 0)
        {
            var named = new CsvColumns(
                Column(header, mapping, "titleColumn", problems),
                Column(header, mapping, "artistColumn", problems),
                Column(header, mapping, "albumColumn", problems),
                Column(header, mapping, "durationColumn", problems),
                Column(header, mapping, "isrcColumn", problems),
                Column(header, mapping, "mbidColumn", problems),
                Id: -1);

            if (named is { Title: < 0, Isrc: < 0, Mbid: < 0 } && problems.Count == 0)
            {
                problems.Add("Name the title column (or an ISRC or MusicBrainz id column).");
            }

            return (GenericFormat, named);
        }

        // Exportify in English (and older Exportify versions, which wrote "Spotify ID" and "Duration (ms)").
        var title = Find(header, "Track Name");
        if (title >= 0)
        {
            return (ExportifyFormat, new CsvColumns(
                title,
                Find(header, "Artist Name(s)"),
                Find(header, "Album Name"),
                Find(header, "Track Duration (ms)", "Duration (ms)"),
                Find(header, "ISRC"),
                Mbid: -1,
                Find(header, "Track URI", "Spotify ID")));
        }

        // Exportify in another language: the URIs give it away, and the base columns keep their places.
        if (firstRow is { Length: > 0 }
            && firstRow[0].Trim().StartsWith(SpotifyTrackPrefix, StringComparison.Ordinal)
            && header.Length > ExportifyPositions.Isrc)
        {
            return (ExportifyFormat, ExportifyPositions);
        }

        // Anything else: the usual names, or the user maps the columns.
        var guessed = new CsvColumns(
            Find(header, "Title", "Track", "Track Title", "Song", "Song Title", "Name"),
            Find(header, "Artist", "Artists", "Artist Name", "Performer"),
            Find(header, "Album", "Album Title", "Release"),
            Find(header, "Duration", "Length", "Time", "Duration (ms)", "Length (ms)"),
            Find(header, "ISRC"),
            Find(header, "MBID", "MusicBrainz Recording Id", "MusicBrainz Track Id", "Recording MBID"),
            Id: -1);

        if (guessed is { Title: < 0, Isrc: < 0, Mbid: < 0 })
        {
            problems.Add(string.Concat(
                "The title column could not be found; name it in the column fields. The file's columns are: ",
                string.Join(", ", header.Where(cell => cell.Length > 0)),
                "."));

            return (GenericFormat, null);
        }

        return (GenericFormat, guessed);
    }

    private static ImportListEntry? Entry(string[] row, CsvColumns columns, string format)
    {
        var title = Cell(row, columns.Title);
        var artist = Cell(row, columns.Artist);
        var isrc = Isrc(Cell(row, columns.Isrc));
        var mbid = Mbid(Cell(row, columns.Mbid));

        if (title is null && isrc is null && mbid is null)
        {
            return null;
        }

        // Exportify joins several artists with commas; the first is the main one, and the text
        // lookup — used only when the ISRC does not resolve — does better with it alone.
        if (format == ExportifyFormat && artist is not null && artist.Contains(',', StringComparison.Ordinal))
        {
            artist = artist[..artist.IndexOf(',', StringComparison.Ordinal)].Trim();
        }

        var id = Cell(row, columns.Id);
        var externalId = id
            ?? (isrc is not null ? "isrc:" + isrc : null)
            ?? (mbid is not null ? "mbid:" + mbid : null)
            ?? string.Concat("text:", artist?.ToLowerInvariant(), "|", title!.ToLowerInvariant());

        return new ImportListEntry(
            externalId,
            artist,
            title,
            Cell(row, columns.Album),
            Duration(Cell(row, columns.Duration), format),
            isrc,
            mbid);
    }

    private static int? Duration(string? value, string format)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            var seconds = 0d;

            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
                {
                    return null;
                }

                seconds = (seconds * 60) + number;
            }

            return (int)Math.Round(seconds * 1000);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            return null;
        }

        // Exportify writes milliseconds; elsewhere a number below 10 000 is seconds (no song is
        // ten seconds long, and no song lasts ten thousand).
        return format == ExportifyFormat || length >= 10_000
            ? (int)Math.Round(length)
            : (int)Math.Round(length * 1000);
    }

    private static string? Isrc(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var compact = value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        return compact.Length == 12 && compact.All(char.IsLetterOrDigit) ? compact : null;
    }

    private static string? Mbid(string? value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D", CultureInfo.InvariantCulture) : null;

    private static string? Cell(string[] row, int index)
    {
        if (index < 0 || index >= row.Length)
        {
            return null;
        }

        var value = row[index].Trim();

        return value.Length == 0 ? null : value;
    }

    private static int Find(string[] header, params string[] names)
    {
        foreach (var name in names)
        {
            for (var index = 0; index < header.Length; index++)
            {
                if (string.Equals(header[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static int Column(string[] header, Dictionary<string, string> mapping, string key, List<string> problems)
    {
        if (!mapping.TryGetValue(key, out var name))
        {
            return -1;
        }

        var index = Find(header, name);

        if (index < 0)
        {
            problems.Add($"The file has no column '{name}' ({key}).");
        }

        return index;
    }

    private static Dictionary<string, string> ReadMapping(JsonElement settings)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

        if (settings.ValueKind != JsonValueKind.Object)
        {
            return mapping;
        }

        foreach (var key in SettingKeys)
        {
            if (settings.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } name
                && name.Trim().Length > 0)
            {
                mapping[key] = name.Trim();
            }
        }

        return mapping;
    }

    /// <summary>Column positions; -1 where the file has no such column.</summary>
    private sealed record CsvColumns(int Title, int Artist, int Album, int Duration, int Isrc, int Mbid, int Id);

    private sealed record ReadResult(
        string Format,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ImportListEntry> Entries,
        IReadOnlyList<string> Problems);
}
