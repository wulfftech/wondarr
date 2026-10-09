using Wondarr.Core.Domain;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.Importing;

/// <summary>
/// Maps the song, its album context and the file's AcoustID onto the Plex-safe tag set of
/// LIBRARY_OUTPUT §7.3–7.5. Pure: it reads the entities it is handed and nothing else.
/// </summary>
public static class TagSetBuilder
{
    /// <summary>
    /// The MusicBrainz id of "Various Artists", which is what Plex reads a compilation from
    /// (LIBRARY_OUTPUT §7.4): it ignores the iTunes compilation flag. It is a fixed id in
    /// MusicBrainz, not a made-up one.
    /// </summary>
    public const string VariousArtistsMbId = "89ad4ac3-39f7-470e-963a-56509c546377";

    /// <summary>The status every Wondarr release context is tagged as (LIBRARY_OUTPUT §7.5).</summary>
    public const string OfficialStatus = "official";

    /// <summary>Builds the tag set for one imported file.</summary>
    /// <param name="song">The song the file satisfies.</param>
    /// <param name="album">The album context the song is filed under.</param>
    /// <param name="artists">Every credited artist, main first.</param>
    /// <param name="acoustId">The AcoustID the file was identified by, or <see langword="null"/>.</param>
    /// <param name="cover">The front cover to embed, or <see langword="null"/> for none.</param>
    /// <param name="replayGainDb">The ReplayGain track gain to write, or <see langword="null"/> for none.</param>
    /// <param name="replayGainPeak">The ReplayGain true peak to write; only used together with the gain.</param>
    /// <returns>The complete tag set; the writer replaces the file's tags with it.</returns>
    public static TagSet Build(
        Song song,
        AlbumContext album,
        IReadOnlyList<(Artist Artist, ArtistRole Role)> artists,
        string? acoustId,
        byte[]? cover,
        double? replayGainDb = null,
        double? replayGainPeak = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(artists);

        var primaryArtist = Primary(artists);

        return new TagSet
        {
            Title = song.Title,
            Artist = song.ArtistCredit,
            Artists = [.. artists.Select(credit => credit.Artist.Name)],
            AlbumArtist = album.AlbumArtist,
            Album = album.AlbumTitle,

            // A disc total of null means "one disc": the tag is written only when a disc number says
            // the release has more than one, which is what LIBRARY_OUTPUT §7.5 asks for.
            TrackNumber = album.TrackNo,
            TrackTotal = album.TotalTracks,
            DiscNumber = album.DiscNo,
            DiscTotal = album.DiscNo is null ? null : 1,

            Date = album.Date,
            OriginalDate = album.OriginalDate,
            Isrc = song.Isrcs.Count > 0 ? song.Isrcs[0] : null,
            MbRecordingId = song.MbRecordingId,

            // One release id per folder: the chosen release's own id, or the synthetic pseudo-album
            // UUID. Plex fragments an album folder whose files disagree (§7.3).
            MbReleaseId = album.AlbumKey,
            MbReleaseGroupId = album.MbReleaseGroupId,
            MbArtistId = primaryArtist?.MbArtistId,
            MbAlbumArtistId = album.IsVariousArtists
                ? VariousArtistsMbId
                : primaryArtist?.MbArtistId,
            ReleaseType = ReleaseType(album.Kind),
            ReleaseStatus = OfficialStatus,
            AcoustId = acoustId,
            Compilation = album.IsVariousArtists,
            FrontCover = cover,
            ReplayGainTrackGainDb = replayGainDb is not null && replayGainPeak is not null ? replayGainDb : null,
            ReplayGainTrackPeak = replayGainDb is not null && replayGainPeak is not null ? replayGainPeak : null,
        };
    }

    /// <summary>The artist the recording is credited to: the main credit, or the first one there is.</summary>
    private static Artist? Primary(IReadOnlyList<(Artist Artist, ArtistRole Role)> artists)
    {
        foreach (var credit in artists)
        {
            if (credit.Role == ArtistRole.Main)
            {
                return credit.Artist;
            }
        }

        return artists.Count > 0 ? artists[0].Artist : null;
    }

    /// <summary>
    /// The MusicBrainz release type of a context. A pseudo-album of loose singles is a real album as
    /// far as a player is concerned — there is no "pseudo" release type to tag it with.
    /// </summary>
    private static string ReleaseType(AlbumContextKind kind) => kind switch
    {
        AlbumContextKind.Single => "single",
        AlbumContextKind.Ep => "ep",
        AlbumContextKind.Compilation => "compilation",
        _ => "album",
    };
}
