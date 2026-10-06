using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Turns one InnerTube <c>search</c> response into results (research_youtube.md §0.1, the path
/// ytmusicapi's <c>mixins/search.py</c> walks). Pure code: no HTTP, no scoring, no rejection — every
/// result is returned as the response stated it, and the caller decides what a grab may use.
/// </summary>
/// <remarks>
/// <para>
/// The shelves live under
/// <c>contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[]</c>:
/// a <c>musicShelfRenderer</c> is a filter's results, an <c>itemSectionRenderer</c> holds the loosely
/// related results an ISRC query returns, and a <c>musicCardShelfRenderer</c> is the top result.
/// </para>
/// <para>
/// Per item the video id and the music video type come from the play button's watch endpoint (the
/// title run's endpoint is the fallback), the title from the first flex column, and the artists, the
/// album and the duration from the credit runs: a run with a channel browse id (<c>UC…</c>) is an
/// artist, one with any other browse id is the album, and a run that looks like a time is the
/// duration. Separators, play counts and view counts carry neither, so they are skipped.
/// </para>
/// </remarks>
public static class InnertubeResponseParser
{
    /// <summary>A duration as InnerTube shows it: M:SS, MM:SS or H:MM:SS.</summary>
    private static readonly Regex DurationRegex = new(@"^(\d+:)*\d+:\d+$", RegexOptions.CultureInvariant);

    /// <summary>The browse id prefix of a channel: an artist page, or a user's channel.</summary>
    private const string ChannelBrowseIdPrefix = "UC";

    /// <summary>The flex column that names the result.</summary>
    private const int TitleColumn = 0;

    /// <summary>The flex column that credits the artists, the album and the duration.</summary>
    private const int CreditColumn = 1;

    /// <summary>Parses a recorded or live response body.</summary>
    /// <param name="json">The response body.</param>
    /// <param name="query">The search text, recorded on the result.</param>
    /// <param name="filter">The shelf that was asked for, recorded on the result.</param>
    public static InnertubeSearchResult Parse(string json, string query, InnertubeSearchFilter filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        return Parse(document.RootElement, query, filter);
    }

    private static InnertubeSearchResult Parse(JsonElement root, string query, InnertubeSearchFilter filter)
    {
        if (!TryGet(root, "contents", out var contents) ||
            !TryGet(contents, "tabbedSearchResultsRenderer", out var tabbed) ||
            !TryGet(tabbed, "tabs", out var tabs) ||
            tabs.ValueKind != JsonValueKind.Array ||
            tabs.GetArrayLength() == 0 ||
            !TryGet(tabs[0], "tabRenderer", out var tab) ||
            !TryGet(tab, "content", out var content) ||
            !TryGet(content, "sectionListRenderer", out var sectionList) ||
            !TryGet(sectionList, "contents", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return InnertubeSearchResult.Empty(query, filter);
        }

        InnertubeResult? topResult = null;
        var results = new List<InnertubeResult>();

        foreach (var section in sections.EnumerateArray())
        {
            if (TryGet(section, "musicCardShelfRenderer", out var card))
            {
                topResult ??= ParseCard(card);
            }
            else if (TryGet(section, "musicShelfRenderer", out var shelf))
            {
                AddItems(shelf, results);
            }
            else if (TryGet(section, "itemSectionRenderer", out var itemSection))
            {
                AddItems(itemSection, results);
            }
        }

        return new InnertubeSearchResult(query, filter, topResult, results);
    }

    /// <summary>The <c>musicCardShelfRenderer</c> top result: the answer an ISRC query returns.</summary>
    private static InnertubeResult? ParseCard(JsonElement card)
    {
        var (videoId, musicVideoType) = CardEndpoint(card);

        if (videoId is null)
        {
            return null;
        }

        var title = CardTitle(card) is { } titleRun ? Text(titleRun, "text") : null;
        var (artists, album, durationMs) = ParseRuns(card, "subtitle");

        return new InnertubeResult(
            videoId,
            title ?? videoId,
            artists,
            album,
            durationMs,
            musicVideoType,
            HasExplicitBadge(card));
    }

    /// <summary>One <c>musicResponsiveListItemRenderer</c>: a shelf item or a loose item-section result.</summary>
    private static InnertubeResult? ParseItem(JsonElement item)
    {
        var (videoId, musicVideoType) = WatchEndpoint(item);

        if (videoId is null)
        {
            return null;
        }

        var title = FirstRunText(item, TitleColumn);
        var (artists, album, durationMs) = ParseRuns(item, CreditColumn);

        return new InnertubeResult(
            videoId,
            title ?? videoId,
            artists,
            album,
            durationMs,
            musicVideoType,
            HasExplicitBadge(item));
    }

    /// <summary>
    /// The video id and music video type of a shelf item: the play button's endpoint when the item has
    /// one (it is also where the music video type lives), otherwise the title run's endpoint.
    /// </summary>
    private static (string? VideoId, string? MusicVideoType) WatchEndpoint(JsonElement item)
    {
        if (TryGet(item, "overlay", out var overlay) &&
            TryGet(overlay, "musicItemThumbnailOverlayRenderer", out var thumbnail) &&
            TryGet(thumbnail, "content", out var content) &&
            TryGet(content, "musicPlayButtonRenderer", out var play) &&
            TryGet(play, "playNavigationEndpoint", out var playEndpoint) &&
            TryGet(playEndpoint, "watchEndpoint", out var watch))
        {
            return (Text(watch, "videoId"), MusicVideoType(watch));
        }

        var titleRuns = Runs(item, TitleColumn);

        if (titleRuns is { } runs &&
            runs.GetArrayLength() > 0 &&
            TryGet(runs[0], "navigationEndpoint", out var endpoint) &&
            TryGet(endpoint, "watchEndpoint", out var titleWatch))
        {
            return (Text(titleWatch, "videoId"), MusicVideoType(titleWatch));
        }

        return (null, null);
    }

    /// <summary>The card's video id: the title run's endpoint, then <c>onTap</c>, then the play button.</summary>
    private static (string? VideoId, string? MusicVideoType) CardEndpoint(JsonElement card)
    {
        if (CardTitle(card) is { } titleRun &&
            TryGet(titleRun, "navigationEndpoint", out var endpoint) &&
            TryGet(endpoint, "watchEndpoint", out var watch))
        {
            return (Text(watch, "videoId"), MusicVideoType(watch));
        }

        if (TryGet(card, "onTap", out var onTap) &&
            TryGet(onTap, "watchEndpoint", out var tapWatch))
        {
            return (Text(tapWatch, "videoId"), MusicVideoType(tapWatch));
        }

        if (TryGet(card, "thumbnailOverlay", out var overlay) &&
            TryGet(overlay, "musicItemThumbnailOverlayRenderer", out var thumbnail) &&
            TryGet(thumbnail, "content", out var content) &&
            TryGet(content, "musicPlayButtonRenderer", out var play) &&
            TryGet(play, "playNavigationEndpoint", out var playEndpoint) &&
            TryGet(playEndpoint, "watchEndpoint", out var overlayWatch))
        {
            return (Text(overlayWatch, "videoId"), MusicVideoType(overlayWatch));
        }

        return (null, null);
    }

    /// <summary>The <c>musicVideoType</c> a watch endpoint carries, when it does.</summary>
    private static string? MusicVideoType(JsonElement watch) =>
        TryGet(watch, "watchEndpointMusicSupportedConfigs", out var configs) &&
        TryGet(configs, "watchEndpointMusicConfig", out var config)
            ? Text(config, "musicVideoType")
            : null;

    /// <summary>The card's title run, when the card names its result.</summary>
    private static JsonElement? CardTitle(JsonElement card)
    {
        if (!TryGet(card, "title", out var title) ||
            !TryGet(title, "runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array ||
            runs.GetArrayLength() == 0)
        {
            return null;
        }

        return runs[0];
    }

    /// <summary>The text of the first run of one flex column, when the item has that column.</summary>
    private static string? FirstRunText(JsonElement item, int column) =>
        Runs(item, column) is { } runs && runs.GetArrayLength() > 0 ? Text(runs[0], "text") : null;

    /// <summary>The credit runs of one flex column.</summary>
    private static (List<string> Artists, string? Album, int? DurationMs) ParseRuns(JsonElement item, int column)
    {
        var runs = Runs(item, column);
        return runs is null ? ([], null, null) : ReadRuns(runs.Value);
    }

    /// <summary>The credit runs of a card's subtitle.</summary>
    private static (List<string> Artists, string? Album, int? DurationMs) ParseRuns(JsonElement card, string property)
    {
        var runs = PropertyRuns(card, property);
        return runs is null ? ([], null, null) : ReadRuns(runs.Value);
    }

    /// <summary>
    /// The artists are the runs with a channel browse id, the album is the run with any other browse
    /// id, and the duration is the last run that looks like a time. Separators, play counts and view
    /// counts carry neither, so they are skipped.
    /// </summary>
    private static (List<string> Artists, string? Album, int? DurationMs) ReadRuns(JsonElement runs)
    {
        var artists = new List<string>();
        string? album = null;
        int? durationMs = null;

        foreach (var run in runs.EnumerateArray())
        {
            var text = Text(run, "text");

            if (TryGet(run, "navigationEndpoint", out var endpoint) &&
                TryGet(endpoint, "browseEndpoint", out var browse))
            {
                var browseId = Text(browse, "browseId");

                if (browseId is null)
                {
                    continue;
                }

                if (browseId.StartsWith(ChannelBrowseIdPrefix, StringComparison.Ordinal))
                {
                    artists.Add(text ?? string.Empty);
                }
                else
                {
                    album = text;
                }

                continue;
            }

            if (text is not null && DurationRegex.IsMatch(text))
            {
                durationMs = ParseDuration(text);
            }
        }

        return (artists, album, durationMs);
    }

    /// <summary>The runs of one flex column, when the item has it.</summary>
    private static JsonElement? Runs(JsonElement item, int column)
    {
        if (!TryGet(item, "flexColumns", out var columns) ||
            columns.ValueKind != JsonValueKind.Array ||
            column >= columns.GetArrayLength() ||
            !TryGet(columns[column], "musicResponsiveListItemFlexColumnRenderer", out var renderer) ||
            !TryGet(renderer, "text", out var text) ||
            !TryGet(text, "runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return runs;
    }

    /// <summary>The runs of one named property, when there are any.</summary>
    private static JsonElement? PropertyRuns(JsonElement element, string property)
    {
        if (!TryGet(element, property, out var value) ||
            !TryGet(value, "runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return runs;
    }

    /// <summary>Every <c>musicResponsiveListItemRenderer</c> of one shelf, skipping the ones without a video id.</summary>
    private static void AddItems(JsonElement section, List<InnertubeResult> results)
    {
        if (!TryGet(section, "contents", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (TryGet(item, "musicResponsiveListItemRenderer", out var renderer) &&
                ParseItem(renderer) is { } result)
            {
                results.Add(result);
            }
        }
    }

    /// <summary>Whether the item carries the explicit badge (ytmusicapi's BADGE_LABEL path).</summary>
    private static bool HasExplicitBadge(JsonElement item)
    {
        if (!TryGet(item, "badges", out var badges) || badges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var badge in badges.EnumerateArray())
        {
            if (TryGet(badge, "musicInlineBadgeRenderer", out var inline) &&
                TryGet(inline, "accessibilityData", out var accessibility) &&
                TryGet(accessibility, "accessibilityData", out var label) &&
                Text(label, "label") is { } text &&
                text.Contains("Explicit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>The string value of one property, when it is a string.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The value of one property, whatever kind it is — the callers check <see cref="JsonValueKind"/>
    /// where it matters (an array property like <c>tabs</c> or <c>contents</c> is as valid as an object).
    /// </summary>
    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value);
    }
}
