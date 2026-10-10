using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Decisions;

/// <summary>
/// Reads the song title out of a candidate's <em>file name</em> and measures how close it is to the
/// wanted song's title. Used by the decision engine's title floor and its compilation-path rule
/// (MATCHING_ENGINE.md §6.2). Folder names never count: an album folder called "The Story Of Cadet
/// Records" says nothing about the track inside it.
/// </summary>
public static class CandidateTitleMatcher
{
    /// <summary>The two separators a remote path can use, whichever platform the source runs on.</summary>
    private static readonly char[] PathSeparators = ['\\', '/'];

    /// <summary>A leading track number and its separator: <c>04 </c>, <c>04. </c>, <c>04 - </c>, <c>A1 </c>, <c>1-06 </c>.</summary>
    private static readonly Regex TrackPrefixRegex = new(
        @"^[A-D]?\d{1,3}(?:[-.]\d{1,3})?(?:\s*[-.)]\s*|\s+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A segment separator: hyphen, en or em dash, surrounded by whitespace.</summary>
    private static readonly Regex DashSeparatorRegex = new(@"\s[-–—]\s", RegexOptions.CultureInvariant);

    /// <summary>One bracketed group.</summary>
    private static readonly Regex BracketRegex = new(@"\(([^()]*)\)|\[([^\[\]]*)\]", RegexOptions.CultureInvariant);

    /// <summary>
    /// The title part of a remote path's file name: the extension, a leading track number and any
    /// artist prefix removed, brackets and version hints left in. <c>04 - Nine Days - Absolutely (Story
    /// of a Girl).flac</c> gives <c>Absolutely (Story of a Girl)</c>; <c>03. Title.mp3</c> gives
    /// <c>Title</c>. <c>null</c> when the path has no file name.
    /// </summary>
    /// <param name="remotePath">A Soulseek-style (<c>\</c>) or POSIX (<c>/</c>) path, or a bare file name.</param>
    public static string? ExtractTitle(string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        var segments = TitleSegments(remotePath, 1);

        return segments.Count > 0 ? segments[^1] : null;
    }

    /// <summary>The comparison texts of a song title, worked out once per search and reused for every candidate.</summary>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    public static IReadOnlyList<string> SongVariants(string songTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);

        return Variants(songTitle)
            .Where(text => TextMatching.Normalize(text).Length > 0)
            .ToList();
    }

    /// <summary>
    /// How closely a candidate's own track title matches the song's title, from 0 to 1, or
    /// <c>null</c> when the candidate carries no title of its own (a torrent or NZB whose file list is
    /// still unknown, where the path is a release name).
    /// </summary>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    /// <param name="candidate">The candidate.</param>
    public static double? Similarity(string songTitle, Candidate candidate) =>
        Similarity(SongVariants(songTitle), candidate);

    /// <summary>The same, with the song's variants from <see cref="SongVariants"/>.</summary>
    /// <param name="songVariants">The song title's comparison texts.</param>
    /// <param name="candidate">The candidate.</param>
    /// <remarks>
    /// <para>
    /// The best of every pairing of a song text with a candidate text. A candidate text is the parser's
    /// title and the file name (one or two leading track numbers stripped, since "21 Guns" starts with
    /// one), each also cut at its dashes so that an artist prefix or suffix falls away ("Johnny Cash -
    /// Hurt", "Hey Jude - The Beatles"), and each without hints, <c>feat.</c> clauses and brackets, plus
    /// the content of each bracket group. MusicBrainz lists a song as "Story of a Girl" and as
    /// "Absolutely (Story of a Girl)", so the one holding the other as a parenthesised part matches it.
    /// </para>
    /// <para>
    /// A pair scores its token-sort similarity (<see cref="TextMatching.Similarity"/>), or when every
    /// word of the shorter text is in the longer one: 1.0 as a contiguous run, 0.9 scattered ("Symphony
    /// No.5 - I. Allegro con brio" against the full MusicBrainz title of the movement).
    /// </para>
    /// </remarks>
    public static double? Similarity(IReadOnlyList<string> songVariants, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(songVariants);
        ArgumentNullException.ThrowIfNull(candidate);

        var parsedTitle = candidate.Parsed.Title;
        var hasParsedTitle = !string.IsNullOrWhiteSpace(parsedTitle);

        if (!hasParsedTitle && candidate.Container != CandidateContainer.SingleFile)
        {
            return null;
        }

        var candidateTexts = new List<string>();

        if (hasParsedTitle)
        {
            AddWithDashSuffixes(candidateTexts, parsedTitle!);
        }

        for (var passes = 1; passes <= 2; passes++)
        {
            foreach (var segment in JoinedSuffixes(TitleSegments(candidate.RemotePath, passes)))
            {
                candidateTexts.AddRange(Variants(segment));
            }
        }

        var candidateTokens = candidateTexts
            .Select(text => TextMatching.Normalize(text))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidateTokens.Count == 0 || songVariants.Count == 0)
        {
            return null;
        }

        var best = 0.0;

        foreach (var candidateText in candidateTokens)
        {
            foreach (var songVariant in songVariants)
            {
                best = Math.Max(best, PairScore(TextMatching.Normalize(songVariant), candidateText));

                if (best >= 1.0)
                {
                    return best;
                }
            }
        }

        return best;
    }

    /// <summary>Whether a title is written in Latin letters, in others, or has no letters at all.</summary>
    /// <param name="title">The title.</param>
    /// <returns><c>true</c> for Latin, <c>false</c> for another script, <c>null</c> when no letter is in it.</returns>
    public static bool? IsLatinScript(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var sawOther = false;

        foreach (var character in title)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            if (character < 'ɐ' || character is >= 'Ḁ' and <= 'ỿ')
            {
                return true;
            }

            sawOther = true;
        }

        return sawOther ? false : null;
    }

    /// <summary>Token-sort similarity of two normalised texts, or the containment score when one holds the other's words.</summary>
    private static double PairScore(string song, string candidate)
    {
        var similarity = TextMatching.Similarity(song, candidate);
        var songWords = song.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (songWords.Length == 0 || candidateWords.Length == 0)
        {
            return similarity;
        }

        var (shorter, longer, shorterText, longerText) = songWords.Length <= candidateWords.Length
            ? (songWords, candidateWords, song, candidate)
            : (candidateWords, songWords, candidate, song);

        if (!shorter.All(longer.Contains))
        {
            return similarity;
        }

        var contiguous = string.Concat(" ", longerText, " ").Contains(string.Concat(" ", shorterText, " "), StringComparison.Ordinal);

        return Math.Max(similarity, contiguous ? 1.0 : 0.9);
    }

    /// <summary>The text, and each of its dash-separated tails ("Artist - Title" also gives "Title").</summary>
    private static void AddWithDashSuffixes(List<string> texts, string text)
    {
        var parts = DashSeparatorRegex.Split(text).Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

        foreach (var suffix in JoinedSuffixes(parts))
        {
            texts.AddRange(Variants(suffix));
        }

        texts.AddRange(Variants(text));
    }

    /// <summary>The dash-separated parts of the file name once the extension and track number are gone.</summary>
    private static List<string> TitleSegments(string remotePath, int passes)
    {
        var segments = remotePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return [];
        }

        var stem = StripExtension(segments[^1]);

        if (!stem.Contains(' ', StringComparison.Ordinal))
        {
            stem = stem.Replace('_', ' ');
        }

        // Callers try one and two passes: "05 - 21 Guns" is a title that starts with a number, and
        // "1-04 04 Title" a double prefix; only the comparison can tell them apart.
        for (var pass = 0; pass < passes; pass++)
        {
            var match = TrackPrefixRegex.Match(stem);

            if (!match.Success || match.Length >= stem.Length)
            {
                break;
            }

            stem = stem[match.Length..];
        }

        return DashSeparatorRegex.Split(stem)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>"c", "b - c", "a - b - c": a title with a dash in it is still one of these.</summary>
    private static IEnumerable<string> JoinedSuffixes(List<string> segments)
    {
        for (var start = segments.Count - 1; start >= 0; start--)
        {
            yield return string.Join(" - ", segments.Skip(start));
        }
    }

    /// <summary>
    /// The texts one title can be compared as: whole, without hints and <c>feat.</c> clauses, without
    /// its bracketed groups, and each bracketed group's own content.
    /// </summary>
    private static IEnumerable<string> Variants(string text)
    {
        yield return text;
        yield return VersionFlagParser.Parse(text).BaseTitle;
        yield return BracketRegex.Replace(text, " ");

        foreach (Match group in BracketRegex.Matches(text))
        {
            var inner = group.Groups[1].Success ? group.Groups[1].Value : group.Groups[2].Value;

            if (inner.Trim().Length > 0)
            {
                yield return inner;
                yield return VersionFlagParser.Parse(inner).BaseTitle;
            }
        }
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');

        if (dot > 0 && fileName.Length - dot - 1 is >= 1 and <= 5 && fileName[(dot + 1)..].All(char.IsAsciiLetterOrDigit))
        {
            return fileName[..dot];
        }

        return fileName;
    }
}
