using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.Deezer;

/// <summary>
/// The Deezer playlist import list (ADR-0012): a public playlist's tracks, in playlist order, read
/// through <see cref="IDeezerClient"/>. The provider only reads; the sync resolves each track by its
/// Deezer id, then its ISRC, then its text, and adds it.
/// </summary>
public sealed class DeezerPlaylistProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string DeezerPlaylistType = "deezerPlaylist";

    /// <summary>How many tracks one page asks for; Deezer serves at most 100.</summary>
    internal const int PageSize = 100;

    /// <summary>At most 100 pages: 10 000 tracks, more than any playlist needs.</summary>
    internal const int MaxPages = 100;

    private static readonly NotificationField[] SettingsFields =
    [
        new("playlist", "Playlist", "text", true, "A Deezer playlist link or id; the playlist must be public"),
    ];

    private readonly IDeezerClient _deezer;

    /// <summary>Initialises a new instance of the <see cref="DeezerPlaylistProvider"/> class.</summary>
    /// <param name="deezer">The shared Deezer client (its cache, its quota handler).</param>
    public DeezerPlaylistProvider(IDeezerClient deezer)
    {
        ArgumentNullException.ThrowIfNull(deezer);

        _deezer = deezer;
    }

    /// <inheritdoc />
    public string Type => DeezerPlaylistType;

    /// <inheritdoc />
    public string DisplayName => "Deezer playlist";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        var value = DeezerListSettings.Setting(settings, "playlist");

        if (value is null)
        {
            return ["Enter a Deezer playlist link or id."];
        }

        if (DeezerLink.IsShortLink(value))
        {
            return ["Open the short link in a browser and paste the full playlist link."];
        }

        return DeezerLink.PlaylistId(value) is null
            ? ["That does not look like a Deezer playlist link or id."]
            : [];
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        long playlistId;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            playlistId = DeezerLink.PlaylistId(DeezerListSettings.Setting(settings.RootElement, "playlist")) ?? 0;
        }

        if (playlistId < 1)
        {
            return ImportListFetchResult.Failed("The list has no Deezer playlist to read.");
        }

        var entries = new List<ImportListEntry>();
        var index = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var tracks = await _deezer
                .GetPlaylistTracksAsync(playlistId, index, PageSize, cancellationToken)
                .ConfigureAwait(false);

            if (tracks is null)
            {
                return ImportListFetchResult.Failed("The Deezer playlist was not found, or it is private.");
            }

            foreach (var track in tracks.Data)
            {
                if (track.Id > 0)
                {
                    entries.Add(Entry(track));
                }
            }

            index += tracks.Data.Count;

            // A short page is the last one, and so is the page that reached the total Deezer reported.
            if (tracks.Data.Count < PageSize || index >= tracks.Total)
            {
                break;
            }
        }

        return new ImportListFetchResult(entries);
    }

    private static ImportListEntry Entry(DeezerTrack track) => new(
        string.Create(CultureInfo.InvariantCulture, $"deezer:{track.Id}"),
        NullIfEmpty(track.Artist.Name),
        NullIfEmpty(track.Title),
        NullIfEmpty(track.Album.Title),
        track.Duration > 0 ? track.Duration * 1000 : null,
        NullIfEmpty(track.Isrc),
        DeezerId: track.Id);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
