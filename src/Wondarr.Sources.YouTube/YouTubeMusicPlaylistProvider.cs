using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Notifications;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// The YouTube Music playlist import list (ADR-0012): a public or unlisted playlist's rows, in
/// playlist order, read through <see cref="IInnertubeClient"/>. Reading a playlist is metadata only,
/// so it works whether or not the YouTube source is enabled. The rows carry no ISRC, so the sync
/// resolves each one by its text, with the length check.
/// </summary>
public sealed class YouTubeMusicPlaylistProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string YouTubeMusicPlaylistType = "youtubeMusicPlaylist";

    private static readonly NotificationField[] SettingsFields =
    [
        new(
            "playlist",
            "Playlist",
            "text",
            true,
            "A YouTube Music or YouTube playlist link, or its id; the playlist must be public or unlisted"),
    ];

    private readonly IInnertubeClient _innertube;

    /// <summary>Initialises a new instance of the <see cref="YouTubeMusicPlaylistProvider"/> class.</summary>
    /// <param name="innertube">The shared InnerTube client.</param>
    public YouTubeMusicPlaylistProvider(IInnertubeClient innertube)
    {
        ArgumentNullException.ThrowIfNull(innertube);

        _innertube = innertube;
    }

    /// <inheritdoc />
    public string Type => YouTubeMusicPlaylistType;

    /// <inheritdoc />
    public string DisplayName => "YouTube Music playlist";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        var value = Playlist(settings);

        return value is null
            ? ["Enter a YouTube Music or YouTube playlist link, or its id."]
            : YouTubePlaylistLink.TryParse(value, out _)
                ? []
                : ["That does not look like a YouTube Music or YouTube playlist link or id."];
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string playlistId;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            var value = Playlist(settings.RootElement);

            if (value is null || !YouTubePlaylistLink.TryParse(value, out playlistId))
            {
                return ImportListFetchResult.Failed("The list has no YouTube Music playlist to read.");
            }
        }

        IReadOnlyList<InnertubePlaylistRow> rows;

        try
        {
            rows = await _innertube.BrowsePlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false);
        }
        catch (InnertubeException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return ImportListFetchResult.Failed("The YouTube Music playlist was not found, or it is private.");
        }

        var entries = new List<ImportListEntry>(rows.Count);

        foreach (var row in rows)
        {
            entries.Add(new ImportListEntry(
                string.Concat("ytm:", row.VideoId),
                row.Artist,
                row.Title,
                row.Album,
                row.DurationMs));
        }

        return new ImportListFetchResult(entries);
    }

    private static string? Playlist(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("playlist", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && text.Trim().Length > 0
                ? text.Trim()
                : null;
}

/// <summary>
/// Reads a playlist id out of what the user typed: a bare id (letters, digits, <c>-</c> and <c>_</c>,
/// 10 to 64 characters), a <c>playlist?list=…</c> link on either host, or a <c>watch?v=…&amp;list=…</c>
/// link, whose <c>list</c> parameter is the playlist.
/// </summary>
internal static class YouTubePlaylistLink
{
    /// <summary>A bare playlist id, or the value of a link's <c>list</c> parameter.</summary>
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{10,64}$", RegexOptions.CultureInvariant);

    /// <summary>Reads the playlist id; <see langword="false"/> when the value is neither form.</summary>
    public static bool TryParse(string? value, out string playlistId)
    {
        playlistId = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (IdPattern.IsMatch(text))
        {
            playlistId = text;
            return true;
        }

        // A link without its scheme still parses once one is added: "music.youtube.com/playlist?list=…".
        if (!Uri.TryCreate(
                text.Contains("://", StringComparison.Ordinal) ? text : string.Concat("https://", text),
                UriKind.Absolute,
                out var uri))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?');

        if (query.Length == 0)
        {
            return false;
        }

        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2 && parts[0] == "list" && IdPattern.IsMatch(Uri.UnescapeDataString(parts[1])))
            {
                playlistId = Uri.UnescapeDataString(parts[1]);
                return true;
            }
        }

        return false;
    }
}
