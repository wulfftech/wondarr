using Wondarr.Core.Domain;

namespace Wondarr.Core.Compaction;

/// <summary>An album as the Compact plan names it: enough to show and to match, without the whole context.</summary>
/// <param name="AlbumKey">The grouping key: a real release MBID, or a synthetic pseudo-album UUID.</param>
/// <param name="Kind">Whether the album is real, a single, an EP, a compilation or a pseudo-album.</param>
/// <param name="AlbumTitle">The album title.</param>
/// <param name="AlbumArtist">The album artist.</param>
public sealed record CompactAlbum(string AlbumKey, AlbumContextKind Kind, string AlbumTitle, string AlbumArtist);

/// <summary>
/// One song the Compact task would move: where it is filed now, where the re-plan puts it, and the
/// two paths when the move would touch the disk.
/// </summary>
/// <param name="SongId">The song that would move.</param>
/// <param name="Title">The song's title.</param>
/// <param name="ArtistCredit">The song's display credit.</param>
/// <param name="From">The album the song is filed under now.</param>
/// <param name="To">The album the re-plan gives it.</param>
/// <param name="FromPath">
/// The file's current path, or <see langword="null"/> when the song has no file or a reference file
/// (one that lives outside the library and is never moved).
/// </param>
/// <param name="ToPath">The absolute path the organizer would give the file, or <see langword="null"/> beside <paramref name="FromPath"/>.</param>
/// <param name="Proposed">The album context the move would write; new and untracked, since a plan writes nothing.</param>
public sealed record CompactMove(
    long SongId,
    string Title,
    string ArtistCredit,
    CompactAlbum From,
    CompactAlbum To,
    string? FromPath,
    string? ToPath,
    AlbumContext Proposed);

/// <summary>
/// The whole re-plan of one library: how many albums it holds now and would hold afterwards, how many
/// songs were re-planned, and the moves that would get from one to the other (LIBRARY_OUTPUT.md §7.3).
/// </summary>
/// <param name="LibraryId">The library that was planned.</param>
/// <param name="AlbumsBefore">How many distinct album keys the library's songs hold now.</param>
/// <param name="AlbumsAfter">How many distinct album keys they would hold after the moves.</param>
/// <param name="SongsConsidered">How many songs the re-plan looked at.</param>
/// <param name="Moves">The songs whose album would change, ordered by from-album, then to-album, then song id.</param>
public sealed record CompactPlan(
    long LibraryId,
    int AlbumsBefore,
    int AlbumsAfter,
    int SongsConsidered,
    IReadOnlyList<CompactMove> Moves);
