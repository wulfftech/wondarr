using System.Globalization;
using System.Net;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.ListenBrainz;

/// <summary>
/// The ListenBrainz playlist import list (ADR-0012): one JSPF playlist's tracks, in playlist order,
/// read through the named <c>listenbrainz</c> client. Most tracks carry a recording MBID, so the
/// sync resolves them by it; a track without one is identified by its place in the playlist and
/// resolved by its text.
/// </summary>
public sealed class ListenBrainzPlaylistProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string ListenBrainzPlaylistType = "listenbrainzPlaylist";

    private static readonly NotificationField[] SettingsFields =
    [
        new("playlist", "Playlist", "text", true, "A listenbrainz.org playlist link, or the playlist's MBID; the playlist must be public"),
    ];

    private readonly IHttpClientFactory _factory;

    /// <summary>Initialises a new instance of the <see cref="ListenBrainzPlaylistProvider"/> class.</summary>
    /// <param name="factory">Builds the named <c>listenbrainz</c> client.</param>
    public ListenBrainzPlaylistProvider(IHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    /// <inheritdoc />
    public string Type => ListenBrainzPlaylistType;

    /// <inheritdoc />
    public string DisplayName => "ListenBrainz playlist";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        var value = ListenBrainzSettings.Setting(settings, "playlist");

        return value is null
            ? ["Enter a ListenBrainz playlist link or MBID."]
            : ListenBrainzSettings.PlaylistMbid(value) is null
                ? ["That does not look like a ListenBrainz playlist link or MBID."]
                : [];
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string? mbid;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            mbid = ListenBrainzSettings.PlaylistMbid(ListenBrainzSettings.Setting(settings.RootElement, "playlist"));
        }

        if (mbid is null)
        {
            return ImportListFetchResult.Failed("The list has no ListenBrainz playlist to read.");
        }

        var http = _factory.CreateClient(Metadata.ServiceCollectionExtensions.ListenBrainzClientName);
        var answer = await ListenBrainzFetch
            .GetAsync(http, string.Concat("playlist/", mbid), cancellationToken)
            .ConfigureAwait(false);

        if (answer.Status == HttpStatusCode.NotFound)
        {
            return ImportListFetchResult.Failed("The ListenBrainz playlist was not found, or it is private.");
        }

        if (!answer.Ok)
        {
            return ImportListFetchResult.Failed(answer.Error!);
        }

        var read = ListenBrainzFetch.Playlist(answer.Body);

        if (read?.Playlist?.Track is null)
        {
            return ImportListFetchResult.Failed("ListenBrainz answered a playlist Wondarr could not read.");
        }

        var entries = new List<ImportListEntry>(read.Playlist.Track.Count);

        for (var position = 0; position < read.Playlist.Track.Count; position++)
        {
            if (Entry(read.Playlist.Track[position], position) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return new ImportListFetchResult(entries);
    }

    /// <summary>
    /// The JSPF track as an entry: the recording MBID is the strongest match there is, and a track
    /// without one keeps its place in the playlist so two syncs still agree on what the line is.
    /// </summary>
    private static ImportListEntry? Entry(ListenBrainzJspfTrack track, int position)
    {
        var mbid = ListenBrainzSettings.RecordingMbid(track.Identifier);
        var title = Text(track.Title);

        if (mbid is null && title is null)
        {
            return null;
        }

        return new ImportListEntry(
            string.Concat("lb:", mbid ?? position.ToString(CultureInfo.InvariantCulture)),
            Text(track.Creator),
            title,
            Text(track.Album),
            track.Duration is { } duration && duration > 0 ? (int)duration : null,
            MbRecordingId: mbid);
    }

    /// <summary>A value with nothing but whitespace in it is no value at all.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
