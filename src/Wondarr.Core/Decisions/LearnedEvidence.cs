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
}

/// <summary>
/// A song's learned other titles, folded once per search (<see cref="CandidateTitleMatcher.TitleForms"/>,
/// <see cref="CandidateTitleMatcher.SongVariants"/>) so that each candidate's own title is worked out once
/// however many titles the song has learned.
/// </summary>
public sealed class LearnedTitles
{
    /// <summary>
    /// How close a candidate's title has to be to a learned title to be that recording when its whole title is
    /// not the learned title in full: "Phil Collins - Both Sides - 11 - Please Come Out Tonight FLAC" holds the
    /// title, and the song's own "Please Come Tonight" scores lower on it.
    /// </summary>
    public const double ComparisonFloor = 0.85;

    /// <summary>The shared "nothing learned" instance.</summary>
    public static readonly LearnedTitles None = new([], new HashSet<string>(StringComparer.Ordinal), [], VersionFlags.None);

    private readonly IReadOnlyList<(string Title, IReadOnlySet<string> Forms, IReadOnlyList<string> Variants)> _titles;
    private readonly IReadOnlySet<string> _songForms;
    private readonly IReadOnlyList<string> _songVariants;
    private readonly VersionFlags _songFlags;

    private LearnedTitles(
        IReadOnlyList<(string Title, IReadOnlySet<string> Forms, IReadOnlyList<string> Variants)> titles,
        IReadOnlySet<string> songForms,
        IReadOnlyList<string> songVariants,
        VersionFlags songFlags)
    {
        _titles = titles;
        _songForms = songForms;
        _songVariants = songVariants;
        _songFlags = songFlags;
    }

    /// <summary>Gets a value indicating whether the song has learned no title.</summary>
    public bool IsEmpty => _titles.Count == 0;

    /// <summary>Folds a song's learned titles for one search.</summary>
    /// <param name="otherTitles">The song's learned titles (<see cref="BlocklistKeys.OtherTitles"/>).</param>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    /// <param name="songFlags">The version flags the song carries.</param>
    public static LearnedTitles For(IReadOnlyList<string> otherTitles, string songTitle, VersionFlags songFlags)
    {
        ArgumentNullException.ThrowIfNull(otherTitles);
        ArgumentNullException.ThrowIfNull(songTitle);

        if (otherTitles.Count == 0)
        {
            return None;
        }

        var titles = otherTitles
            .Select(title => (Title: title, Forms: CandidateTitleMatcher.TitleForms(title), Variants: CandidateTitleMatcher.SongVariants(title)))
            .Where(entry => entry.Forms.Count > 0)
            .ToList();

        return new LearnedTitles(
            titles,
            CandidateTitleMatcher.TitleForms(songTitle),
            CandidateTitleMatcher.SongVariants(songTitle),
            songFlags);
    }

    /// <summary>
    /// The learned title a candidate is, or <see langword="null"/>. It is one when its own title is the
    /// learned title in full and not the song's as well (a file can be named both ways, and the song's name
    /// wins), or when its title is at least <see cref="ComparisonFloor"/> close to the learned title and
    /// strictly closer to it than to the song's own (a tie keeps the candidate). A candidate with no title of
    /// its own is never refused by the second rule.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    public string? OtherTitleOf(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (_titles.Count == 0)
        {
            return null;
        }

        if (CandidateTitleMatcher.FullTitleForms(candidate, _songFlags) is { } forms)
        {
            foreach (var (title, titleForms, _) in _titles)
            {
                if (forms.Overlaps(titleForms))
                {
                    return forms.Overlaps(_songForms) ? null : title;
                }
            }
        }

        if (CandidateTitleMatcher.Similarity(_songVariants, candidate) is not { } own)
        {
            return null;
        }

        string? closest = null;
        var closestScore = ComparisonFloor;

        foreach (var (title, _, variants) in _titles)
        {
            if (CandidateTitleMatcher.Similarity(variants, candidate) is { } score && score >= closestScore && score > own)
            {
                closest = title;
                closestScore = score;
            }
        }

        return closest;
    }
}
