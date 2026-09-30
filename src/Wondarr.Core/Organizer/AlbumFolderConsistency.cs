using Wondarr.Core.Domain;

namespace Wondarr.Core.Organizer;

/// <summary>
/// Keeps the files of one album folder agreeing on what the album is. Plex groups by the embedded
/// tags, and a folder whose files disagree on album title, album artist or date is fragmented into
/// several albums (LIBRARY_OUTPUT.md §7.3–7.4), so a file about to be placed takes the folder's own
/// answer when it has a different one.
/// </summary>
public static class AlbumFolderConsistency
{
    /// <summary>
    /// Copies the folder-wide fields from <paramref name="folder"/> onto <paramref name="target"/>
    /// where they differ, and returns the names of the fields it changed.
    /// </summary>
    /// <remarks>
    /// Only the fields that describe the album as a whole are copied. What describes <b>this</b> file
    /// or its provenance — its track and disc number, the track total, the original release date, the
    /// cover it was matched with, its grouping key, its song and whether the assignment is sticky — is
    /// never touched: a sibling must not move a file to another song, folder or cover.
    /// </remarks>
    /// <param name="target">The context of the file about to be tagged and placed; changed in place.</param>
    /// <param name="folder">The context of a file already in the folder it is landing in.</param>
    /// <returns>The names of the fields that were changed, empty when the two already agree.</returns>
    public static IReadOnlyList<string> Align(AlbumContext target, AlbumContext folder)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(folder);

        var changed = new List<string>();

        if (!string.Equals(target.AlbumTitle, folder.AlbumTitle, StringComparison.Ordinal))
        {
            target.AlbumTitle = folder.AlbumTitle;
            changed.Add(nameof(AlbumContext.AlbumTitle));
        }

        if (!string.Equals(target.AlbumArtist, folder.AlbumArtist, StringComparison.Ordinal))
        {
            target.AlbumArtist = folder.AlbumArtist;
            changed.Add(nameof(AlbumContext.AlbumArtist));
        }

        if (!string.Equals(target.Date, folder.Date, StringComparison.Ordinal))
        {
            target.Date = folder.Date;
            changed.Add(nameof(AlbumContext.Date));
        }

        if (!string.Equals(target.MbReleaseId, folder.MbReleaseId, StringComparison.Ordinal))
        {
            target.MbReleaseId = folder.MbReleaseId;
            changed.Add(nameof(AlbumContext.MbReleaseId));
        }

        if (!string.Equals(target.MbReleaseGroupId, folder.MbReleaseGroupId, StringComparison.Ordinal))
        {
            target.MbReleaseGroupId = folder.MbReleaseGroupId;
            changed.Add(nameof(AlbumContext.MbReleaseGroupId));
        }

        if (target.IsVariousArtists != folder.IsVariousArtists)
        {
            target.IsVariousArtists = folder.IsVariousArtists;
            changed.Add(nameof(AlbumContext.IsVariousArtists));
        }

        if (target.Kind != folder.Kind)
        {
            target.Kind = folder.Kind;
            changed.Add(nameof(AlbumContext.Kind));
        }

        return changed;
    }
}