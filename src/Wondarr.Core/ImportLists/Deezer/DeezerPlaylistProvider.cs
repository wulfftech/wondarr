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
        var complete = false;
        var total = 0;
        string? shortRead = null;

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

            // The page that reached the total Deezer reported ends the read. A short page is not the
            // last one (Deezer can drop unavailable tracks from a page). An empty page ends it too: a
            // complete read when it lands near the total, a short read when it lands far under it.
            // Running out of pages before either is a truncated read (the provider's own cap).
            if (tracks.Total > 0 && index >= tracks.Total)
            {
                complete = true;
                break;
            }

            if (tracks.Data.Count == 0)
            {
                // An empty page ends the read. Deezer's total counts tracks it no longer serves, so a
                // read that lands a little under it is whole; one that lands far under it is a short
                // read the sync does not act on.
                total = tracks.Total;
                complete = true;

                if (total > 0 && !SmallGap(total, index))
                {
                    shortRead = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Deezer reported {total} tracks but returned {index}");
                }

                break;
            }
        }

        return new ImportListFetchResult(entries, Truncated: !complete, ShortRead: shortRead);
    }

    /// <summary>Whether a read that returned <paramref name="read"/> of <paramref name="total"/> tracks is close enough to be whole: at least one track, and at most 5 or 10 % missing.</summary>
    internal static bool SmallGap(int total, int read) =>
        read > 0 && (total - read <= 5 || (total - read) * 10 <= total);

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
