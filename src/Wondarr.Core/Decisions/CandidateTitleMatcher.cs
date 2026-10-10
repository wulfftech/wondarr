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

        var segments = TitleSegments(remotePath);

        return segments.Count > 0 ? segments[^1] : null;
    }

    /// <summary>
    /// How closely a candidate's own track title matches the song's title, from 0 to 1, or
    /// <c>null</c> when the candidate carries no title of its own (a torrent or NZB whose file list is
    /// still unknown, where the path is a release name).
    /// </summary>
    /// <param name="songTitle">The song's title as MusicBrainz lists it.</param>
    /// <param name="candidate">The candidate.</param>
    /// <remarks>
    /// Every comparison is a token-sort similarity (<see cref="TextMatching.Similarity"/>) after hints
    /// and <c>feat.</c> clauses are taken off. MusicBrainz lists a song as "Story of a Girl" and as
    /// "Absolutely (Story of a Girl)", so a title that holds the other as a whole parenthesised part
    /// matches it, in either direction. The parser's title is used when there is one; the file name
    /// contributes the parenthesised parts the parser throws away and, when the parser found no title,
    /// everything else.
    /// </remarks>
    public static double? Similarity(string songTitle, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
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
            candidateTexts.Add(parsedTitle!);
        }

        foreach (var segment in JoinedSuffixes(TitleSegments(candidate.RemotePath)))
        {
            candidateTexts.AddRange(Variants(segment, bracketPartsOnly: hasParsedTitle));
        }

        candidateTexts.RemoveAll(text => TextMatching.Normalize(text).Length == 0);

        var songVariants = Variants(songTitle, bracketPartsOnly: false)
            .Where(text => TextMatching.Normalize(text).Length > 0)
            .ToList();

        if (candidateTexts.Count == 0 || songVariants.Count == 0)
        {
            return null;
        }

        var best = 0.0;

        foreach (var candidateText in candidateTexts)
        {
            foreach (var songVariant in songVariants)
            {
                best = Math.Max(best, TextMatching.Similarity(songVariant, candidateText));
            }
        }

        return best;
    }

    /// <summary>The dash-separated parts of the file name once the extension and track number are gone.</summary>
    private static List<string> TitleSegments(string remotePath)
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

        // A double prefix ("1-04 04 Title") costs nothing to handle.
        for (var pass = 0; pass < 2; pass++)
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
    private static IEnumerable<string> Variants(string text, bool bracketPartsOnly)
    {
        if (!bracketPartsOnly)
        {
            yield return text;
            yield return VersionFlagParser.Parse(text).BaseTitle;
            yield return BracketRegex.Replace(text, " ");
        }

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
