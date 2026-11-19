using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Api.MusicVideos;

/// <summary>A song as the Match page's Music videos tab shows it.</summary>
/// <param name="Id">The song id.</param>
/// <param name="Title">The song's title.</param>
/// <param name="ArtistCredit">The artist credit as it is written.</param>
/// <param name="Album">The album title, or <see langword="null"/>.</param>
/// <param name="DurationMs">The song's length in milliseconds, or <see langword="null"/>.</param>
/// <param name="HasVideo">Whether the song already has a video, or one on its way.</param>
public sealed record MusicVideoMatchSongResource(long Id, string Title, string ArtistCredit, string? Album, int? DurationMs, bool HasVideo)
{
    /// <summary>Maps a song.</summary>
    /// <param name="song">The song to map.</param>
    public static MusicVideoMatchSongResource From(MusicVideoMatchSong song)
    {
        ArgumentNullException.ThrowIfNull(song);

        return new MusicVideoMatchSongResource(song.Id, song.Title, song.ArtistCredit, song.Album, song.DurationMs, song.HasVideo);
    }
}

/// <summary>A song a video file might be.</summary>
/// <param name="SongId">The song id.</param>
/// <param name="Title">The song's title, or <see langword="null"/> when the song has been deleted since.</param>
/// <param name="ArtistCredit">The artist credit, or <see langword="null"/> when the song has been deleted since.</param>
/// <param name="Album">The album title, or <see langword="null"/>.</param>
/// <param name="DurationMs">The song's length in milliseconds, or <see langword="null"/>.</param>
/// <param name="Score">The score the scan gave it: the title similarity, plus 0.05 when the length fits.</param>
/// <param name="Reason">Where the name came from, or why the song cannot take the file.</param>
/// <param name="SongHasVideo">Whether the song already has a video, or one on its way.</param>
public sealed record MusicVideoMatchCandidateResource(
    long SongId,
    string? Title,
    string? ArtistCredit,
    string? Album,
    int? DurationMs,
    double Score,
    string Reason,
    bool SongHasVideo);

/// <summary>One video file waiting on the Match page.</summary>
/// <param name="Id">The row id.</param>
/// <param name="State">The state, in the words the <c>state</c> filter uses.</param>
/// <param name="Path">The path under the video root, <c>/</c>-separated.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Container">The container, or <see langword="null"/> when the file was never read.</param>
/// <param name="DurationMs">The length in milliseconds, or <see langword="null"/>.</param>
/// <param name="Width">The picture width, or <see langword="null"/>.</param>
/// <param name="Height">The picture height, or <see langword="null"/>.</param>
/// <param name="ParsedArtist">The artist the file name says.</param>
/// <param name="ParsedTitle">The title the file name says.</param>
/// <param name="NfoArtist">The artist the <c>.nfo</c> beside the file says.</param>
/// <param name="NfoTitle">The title the <c>.nfo</c> beside the file says.</param>
/// <param name="TagArtist">The artist tag of the file.</param>
/// <param name="TagTitle">The title tag of the file.</param>
/// <param name="Candidates">The songs it might be, best first, at most five.</param>
/// <param name="ThumbnailUrl">Where the image beside the file is served, or <see langword="null"/> when there is none.</param>
public sealed record MusicVideoMatchRowResource(
    long Id,
    string State,
    string Path,
    long Size,
    string? Container,
    int? DurationMs,
    int? Width,
    int? Height,
    string? ParsedArtist,
    string? ParsedTitle,
    string? NfoArtist,
    string? NfoTitle,
    string? TagArtist,
    string? TagTitle,
    IReadOnlyList<MusicVideoMatchCandidateResource> Candidates,
    string? ThumbnailUrl)
{
    /// <summary>The wire name of a state: camelCase, like every other enum on the API.</summary>
    /// <param name="state">The state.</param>
    public static string WireName(MusicVideoFileState state)
    {
        var name = state.ToString();

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Maps a row.</summary>
    /// <param name="row">The row to map.</param>
    public static MusicVideoMatchRowResource From(MusicVideoMatchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new MusicVideoMatchRowResource(
            row.Id,
            WireName(row.State),
            row.RelativePath,
            row.Size,
            row.Container,
            row.DurationMs,
            row.Width,
            row.Height,
            row.ParsedArtist,
            row.ParsedTitle,
            row.NfoArtist,
            row.NfoTitle,
            row.TagArtist,
            row.TagTitle,
            [.. row.Candidates.Select(candidate => new MusicVideoMatchCandidateResource(
                candidate.SongId,
                candidate.Song?.Title,
                candidate.Song?.ArtistCredit,
                candidate.Song?.Album,
                candidate.Song?.DurationMs,
                candidate.Score,
                candidate.Reason,
                candidate.Song?.HasVideo ?? false))],
            row.HasThumbnail ? $"/api/v1/musicvideo/match/{row.Id}/thumbnail" : null);
    }
}

/// <summary>The number of video files in each state, for the tab's badge.</summary>
/// <param name="Pending">Waiting to be identified.</param>
/// <param name="Adopted">Linked to their song.</param>
/// <param name="Ambiguous">Waiting for a person to pick.</param>
/// <param name="Unmatched">No song fits.</param>
/// <param name="Ignored">Marked as not a video of the library.</param>
/// <param name="Missing">Gone from the folder.</param>
/// <param name="NeedsAttention">What the badge shows: the ambiguous and the unmatched files.</param>
public sealed record MusicVideoMatchSummaryResource(int Pending, int Adopted, int Ambiguous, int Unmatched, int Ignored, int Missing, int NeedsAttention);

/// <summary>What to do with a file: exactly one of <c>songId</c>, <c>ignore</c> or <c>unignore</c>.</summary>
/// <param name="SongId">The song the file is a video of.</param>
/// <param name="Replace">Whether a video the song already has may be replaced (with <paramref name="SongId"/>).</param>
/// <param name="Ignore">Mark the file as not a video of the library.</param>
/// <param name="Unignore">Put an ignored file back in the queue.</param>
public sealed record MusicVideoMatchResolveResource(long? SongId, bool? Replace, bool? Ignore, bool? Unignore);

/// <summary>What a resolve did.</summary>
/// <param name="State">The file's state now, in the words the <c>state</c> filter uses.</param>
/// <param name="SongId">The song the file is linked to, or <see langword="null"/>.</param>
public sealed record MusicVideoMatchResolvedResource(string State, long? SongId);

/// <summary>A bulk action on many files.</summary>
/// <param name="Ids">The files' row ids.</param>
/// <param name="Action"><c>acceptTop</c> or <c>ignore</c>.</param>
public sealed record MusicVideoMatchBulkResource(IReadOnlyList<long>? Ids, string? Action);

/// <summary>What a bulk action did.</summary>
/// <param name="Requested">How many files were named.</param>
/// <param name="Adopted">How many were linked to their top candidate.</param>
/// <param name="Ignored">How many were ignored.</param>
/// <param name="Skipped">How many were left as they were.</param>
public sealed record MusicVideoMatchBulkResultResource(int Requested, int Adopted, int Ignored, int Skipped);

/// <summary>A scan that was queued.</summary>
/// <param name="CommandId">The queued <c>MusicVideoScan</c> command.</param>
public sealed record MusicVideoScanAcceptedResource(long CommandId);
