using Wondarr.Core.Domain;

namespace Wondarr.Core.Songs;

/// <summary>Turns a loaded song's artist credits into what the tag writer and the organizer take.</summary>
public static class SongCreditExtensions
{
    /// <summary>
    /// Gets the song's credits in credit order (main artist first), as the organizer's request takes them.
    /// </summary>
    /// <param name="song">A song loaded with its credits and their artists.</param>
    /// <returns>Each credited artist with the role they are credited in.</returns>
    public static IReadOnlyList<(Artist Artist, ArtistRole Role)> OrderedCredits(this Song song) =>
        song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => (credit.Artist, credit.Role))
            .ToList();
}
