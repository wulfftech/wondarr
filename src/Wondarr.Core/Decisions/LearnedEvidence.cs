using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Decisions;

/// <summary>
/// What the fingerprint check taught about one song, and how a search uses it (MATCHING_ENGINE.md §6.2):
/// the titles of the other recordings it found in the song's place, and how many files that passed on a
/// compilation path on their title alone were another recording. The import writes both as blocklist rows
/// for the song (<see cref="BlocklistKeys.OtherTitle"/>, <see cref="BlocklistKeys.CompilationMiss"/>), so
/// the user sees them on the Blocklist page and can remove them.
/// </summary>
public static class LearnedEvidence
{
    /// <summary>
    /// How many files that passed on a compilation path only on their full title may turn out to be other
    /// recordings before the song stops accepting such files. A generic title ("Be My Friend", "Out on a
    /// Limb") is on hundreds of compilations by other artists; the second miss says the song is one of those.
    /// </summary>
    public const int CompilationMissLimit = 2;

    /// <summary>
    /// The title to remember when the fingerprint check named a recording called
    /// <paramref name="otherTitle"/> in the song's place, or <see langword="null"/> when it is the song's
    /// own title in another form: the same words, either title without its version hints or brackets, or one
    /// holding the other as a bracketed part ("Absolutely (Story of a Girl)" and "Story of a Girl").
    /// </summary>
    /// <remarks>
    /// A different recording with the song's own title (a cover, another artist's song of that name) teaches
    /// nothing here: its title is the one a right file carries too.
    /// </remarks>
    /// <param name="otherTitle">The title AcoustID gave the recording the file is.</param>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    /// <returns>The recording's title without its version hints, or <see langword="null"/>.</returns>
    public static string? TitleToLearn(string otherTitle, string songTitle)
    {
        ArgumentNullException.ThrowIfNull(otherTitle);
        ArgumentNullException.ThrowIfNull(songTitle);

        var baseTitle = VersionFlagParser.Parse(otherTitle).BaseTitle.Trim();

        if (TextMatching.Normalize(baseTitle).Length == 0)
        {
            return null;
        }

        var songTexts = CandidateTitleMatcher.SongVariants(songTitle)
            .Select(text => TextMatching.Normalize(text))
            .ToHashSet(StringComparer.Ordinal);

        if (songTexts.Count == 0)
        {
            return null;
        }

        var shared = CandidateTitleMatcher.SongVariants(otherTitle)
            .Select(text => TextMatching.Normalize(text))
            .Any(songTexts.Contains);

        return shared ? null : baseTitle;
    }

    /// <summary>
    /// The learned title a candidate's own title is in full, or <see langword="null"/>: none of them, or the
    /// candidate's title is the song's as well (a file can be named both ways, and the song's name wins).
    /// </summary>
    /// <param name="otherTitles">The song's learned titles (<see cref="BlocklistKeys.OtherTitles"/>).</param>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    /// <param name="songFlags">The version flags the song carries.</param>
    /// <param name="candidate">The candidate.</param>
    public static string? OtherTitleOf(
        IReadOnlyList<string> otherTitles,
        string songTitle,
        VersionFlags songFlags,
        Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(otherTitles);
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(candidate);

        foreach (var title in otherTitles)
        {
            if (CandidateTitleMatcher.IsFullTitleMatch(title, candidate, songFlags))
            {
                return CandidateTitleMatcher.IsFullTitleMatch(songTitle, candidate, songFlags) ? null : title;
            }
        }

        return null;
    }
}
