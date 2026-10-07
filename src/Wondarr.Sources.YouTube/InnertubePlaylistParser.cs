using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Turns one InnerTube <c>browse</c> response into a playlist's rows. Page 1 keeps them under
/// <c>contents.twoColumnBrowseResultsRenderer.secondaryContents.sectionListRenderer.contents[0]
/// .musicPlaylistShelfRenderer.contents[]</c>; a continuation answer keeps them under
/// <c>onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems[]</c>. In both, a
/// trailing <c>continuationItemRenderer</c> carries the token of the next page (recorded live
/// 2026-10-07). Pure code: no HTTP, no rejection — every row with a video id is returned as the
/// response stated it, and the caller decides what a grab may use.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>musicResponsiveListItemRenderer</c>: the first flex column's runs are the title, the second
/// column's runs are the artist credit (joined, separators and all), the third column's runs are the
/// album (often empty), the first fixed column's run is the duration as <c>m:ss</c> or <c>h:mm:ss</c>,
/// and <c>playlistItemData.videoId</c> is the id. A missing column is null, never an exception.
/// </para>
/// </remarks>
internal static class InnertubePlaylistParser
{
    /// <summary>A duration as InnerTube shows it: M:SS, MM:SS or H:MM:SS.</summary>
    private static readonly Regex DurationRegex = new(@"^(\d+:)*\d+:\d+$", RegexOptions.CultureInvariant);

    /// <summary>The browse id prefix of a channel: an artist page.</summary>
    private const string ChannelBrowseIdPrefix = "UC";

    /// <summary>The flex column that names the row.</summary>
    private const int TitleColumn = 0;

    /// <summary>The flex column that credits the artists.</summary>
    private const int ArtistColumn = 1;

    /// <summary>The flex column that names the album.</summary>
    private const int AlbumColumn = 2;

    /// <summary>Parses the first page of a playlist browse.</summary>
    /// <param name="root">The response, already parsed.</param>
    /// <returns>The rows in playlist order, and the next page's token when there is one.</returns>
    public static (IReadOnlyList<InnertubePlaylistRow> Rows, string? Continuation) ParseFirstPage(JsonElement root)
    {
        var items = Shelf(root);

        return items is null ? ([], null) : ReadItems(items.Value);
    }

    /// <summary>Parses a continuation answer.</summary>
    /// <param name="root">The response, already parsed.</param>
    /// <returns>The rows in playlist order, and the next page's token when there is one.</returns>
    public static (IReadOnlyList<InnertubePlaylistRow> Rows, string? Continuation) ParseContinuation(JsonElement root)
    {
        if (TryGet(root, "onResponseReceivedActions", out var actions) &&
            actions.ValueKind == JsonValueKind.Array &&
            actions.GetArrayLength() > 0 &&
            TryGet(actions[0], "appendContinuationItemsAction", out var appended) &&
            TryGet(appended, "continuationItems", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return ReadItems(items);
        }

        return ([], null);
    }

    /// <summary>The first page's shelf contents, when the response has them.</summary>
    private static JsonElement? Shelf(JsonElement root)
    {
        if (!TryGet(root, "contents", out var contents) ||
            !TryGet(contents, "twoColumnBrowseResultsRenderer", out var twoColumn) ||
            !TryGet(twoColumn, "secondaryContents", out var secondary) ||
            !TryGet(secondary, "sectionListRenderer", out var sectionList) ||
            !TryGet(sectionList, "contents", out var sections) ||
            sections.ValueKind != JsonValueKind.Array ||
            sections.GetArrayLength() == 0 ||
            !TryGet(sections[0], "musicPlaylistShelfRenderer", out var shelf) ||
            !TryGet(shelf, "contents", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return items;
    }

    /// <summary>Every row of one page, plus the token of the page after it.</summary>
    private static (IReadOnlyList<InnertubePlaylistRow> Rows, string? Continuation) ReadItems(JsonElement items)
    {
        var rows = new List<InnertubePlaylistRow>();
        string? continuation = null;

        foreach (var item in items.EnumerateArray())
        {
            if (TryGet(item, "musicResponsiveListItemRenderer", out var renderer))
            {
                if (Row(renderer) is { } row)
                {
                    rows.Add(row);
                }
            }
            else if (continuation is null && TryGet(item, "continuationItemRenderer", out var continuationItem))
            {
                continuation = Token(continuationItem);
            }
        }

        return (rows, continuation);
    }

    /// <summary>One row; <see langword="null"/> when it has no video id (an unavailable video).</summary>
    private static InnertubePlaylistRow? Row(JsonElement renderer)
    {
        var videoId = Property(renderer, "playlistItemData") is { } itemData ? Text(itemData, "videoId") : null;

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        var title = ColumnText(renderer, TitleColumn);
        var (artist, credit) = Artist(renderer);
        var album = ColumnText(renderer, AlbumColumn);

        return new InnertubePlaylistRow(
            videoId,
            title,
            artist,
            credit,
            album,
            Duration(renderer));
    }

    /// <summary>
    /// The credit column: the runs with a channel browse id are the artists, and the whole column's
    /// text — separators and all — is the credit as YouTube Music shows it.
    /// </summary>
    private static (string? Artist, string? Credit) Artist(JsonElement renderer)
    {
        var runs = Runs(renderer, ArtistColumn);

        if (runs is null)
        {
            return (null, null);
        }

        var credit = new StringBuilder();
        string? artist = null;

        foreach (var run in runs.Value.EnumerateArray())
        {
            var text = Text(run, "text");

            if (text is null)
            {
                continue;
            }

            credit.Append(text);

            if (artist is null && IsArtistRun(run))
            {
                artist = text;
            }
        }

        return (artist, credit.Length > 0 ? credit.ToString() : null);
    }

    /// <summary>Whether one run links to a channel (an artist page).</summary>
    private static bool IsArtistRun(JsonElement run) =>
        TryGet(run, "navigationEndpoint", out var endpoint) &&
        TryGet(endpoint, "browseEndpoint", out var browse) &&
        Text(browse, "browseId") is { } browseId &&
        browseId.StartsWith(ChannelBrowseIdPrefix, StringComparison.Ordinal);

    /// <summary>The whole text of one flex column, when the row has that column.</summary>
    private static string? ColumnText(JsonElement renderer, int column)
    {
        var runs = Runs(renderer, column);

        if (runs is null)
        {
            return null;
        }

        var text = new StringBuilder();

        foreach (var run in runs.Value.EnumerateArray())
        {
            if (Text(run, "text") is { } piece)
            {
                text.Append(piece);
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>The first fixed column's duration, in milliseconds, when the row shows one.</summary>
    private static int? Duration(JsonElement renderer)
    {
        if (!TryGet(renderer, "fixedColumns", out var columns) ||
            columns.ValueKind != JsonValueKind.Array ||
            columns.GetArrayLength() == 0 ||
            !TryGet(columns[0], "musicResponsiveListItemFixedColumnRenderer", out var fixedColumn) ||
            !TryGet(fixedColumn, "text", out var text) ||
            !TryGet(text, "runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array ||
            runs.GetArrayLength() == 0)
        {
            return null;
        }

        var value = Text(runs[0], "text");

        return value is not null && DurationRegex.IsMatch(value) ? ParseDuration(value) : null;
    }

    /// <summary>The next page's token, when the row carries one.</summary>
    private static string? Token(JsonElement continuationItem) =>
        TryGet(continuationItem, "continuationEndpoint", out var endpoint) &&
        TryGet(endpoint, "continuationCommand", out var command)
            ? Text(command, "token")
            : null;

    /// <summary>M:SS, MM:SS or H:MM:SS into milliseconds.</summary>
    private static int? ParseDuration(string text)
    {
        var parts = text.Split(':');

        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var total = 0;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            total = (total * 60) + value;
        }

        return total * 1000;
    }

    /// <summary>The runs of one flex column, when the row has it.</summary>
    private static JsonElement? Runs(JsonElement renderer, int column)
    {
        if (!TryGet(renderer, "flexColumns", out var columns) ||
            columns.ValueKind != JsonValueKind.Array ||
            column >= columns.GetArrayLength() ||
            !TryGet(columns[column], "musicResponsiveListItemFlexColumnRenderer", out var flexColumn) ||
            !TryGet(flexColumn, "text", out var text) ||
            !TryGet(text, "runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return runs;
    }

    /// <summary>The value of one property, whatever kind it is.</summary>
    private static JsonElement? Property(JsonElement element, string property) =>
        TryGet(element, property, out var value) ? value : null;

    /// <summary>The string value of one property, when it is a string.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value);
    }
}
