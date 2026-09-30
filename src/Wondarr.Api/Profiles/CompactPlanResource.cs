using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;

namespace Wondarr.Api.Profiles;

/// <summary>
/// What the Compact library task would do to a library: the album counts either side of the moves,
/// how many songs were re-planned, and the moves themselves. It is a dry run — asking for it changes
/// nothing — so the same shape can be offered to the user before anything is executed.
/// </summary>
/// <param name="LibraryId">The library that was planned.</param>
/// <param name="AlbumsBefore">How many distinct albums the library's songs hold now.</param>
/// <param name="AlbumsAfter">How many they would hold after the moves.</param>
/// <param name="SongsConsidered">How many songs the re-plan looked at.</param>
/// <param name="Moves">The songs whose album would change.</param>
public sealed record CompactPlanResource(
    long LibraryId,
    int AlbumsBefore,
    int AlbumsAfter,
    int SongsConsidered,
    IReadOnlyList<CompactMoveResource> Moves);

/// <summary>One song the Compact task would move.</summary>
/// <param name="SongId">The song that would move.</param>
/// <param name="Title">The song's title.</param>
/// <param name="ArtistCredit">The song's display credit.</param>
/// <param name="From">The album the song is filed under now.</param>
/// <param name="To">The album the re-plan gives it.</param>
/// <param name="FromPath">The file's current path, or <see langword="null"/> when the move touches no file.</param>
/// <param name="ToPath">Where the file would go, or <see langword="null"/> beside <paramref name="FromPath"/>.</param>
public sealed record CompactMoveResource(
    long SongId,
    string Title,
    string ArtistCredit,
    CompactAlbumResource From,
    CompactAlbumResource To,
    string? FromPath,
    string? ToPath);

/// <summary>An album as a compact move names it.</summary>
/// <param name="AlbumKey">The grouping key: a real release MBID, or a synthetic pseudo-album UUID.</param>
/// <param name="Kind">Whether the album is real, a single, an EP, a compilation or a pseudo-album.</param>
/// <param name="AlbumTitle">The album title.</param>
/// <param name="AlbumArtist">The album artist.</param>
public sealed record CompactAlbumResource(
    string AlbumKey,
    AlbumContextKind Kind,
    string AlbumTitle,
    string AlbumArtist);

/// <summary>Maps the planner's plan to the shape the API returns.</summary>
public static class CompactPlanResourceExtensions
{
    /// <summary>Maps a plan. The proposed album contexts are deliberately left out: a plan is not a write.</summary>
    /// <param name="plan">The plan to map.</param>
    public static CompactPlanResource ToResource(this CompactPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new CompactPlanResource(
            plan.LibraryId,
            plan.AlbumsBefore,
            plan.AlbumsAfter,
            plan.SongsConsidered,
            [.. plan.Moves.Select(move => move.ToResource())]);
    }

    /// <summary>Maps one move.</summary>
    /// <param name="move">The move to map.</param>
    public static CompactMoveResource ToResource(this CompactMove move)
    {
        ArgumentNullException.ThrowIfNull(move);

        return new CompactMoveResource(
            move.SongId,
            move.Title,
            move.ArtistCredit,
            move.From.ToResource(),
            move.To.ToResource(),
            move.FromPath,
            move.ToPath);
    }

    /// <summary>Maps one album.</summary>
    /// <param name="album">The album to map.</param>
    public static CompactAlbumResource ToResource(this CompactAlbum album)
    {
        ArgumentNullException.ThrowIfNull(album);

        return new CompactAlbumResource(album.AlbumKey, album.Kind, album.AlbumTitle, album.AlbumArtist);
    }
}
