using Wondarr.Core.Metadata;
using System.Globalization;
using System.Text;

namespace Wondarr.Core.Identity;

/// <summary>
/// Normalises and compares the text a user types against the text the providers return
/// (MATCHING_ENGINE.md §6.1).
/// </summary>
/// <remarks>
/// Everything here is deliberately lossy: "Beyoncé &amp; Jay-Z", "Beyonce and Jay Z" and
/// "BEYONCE AND JAY Z" are the same string as far as matching is concerned.
/// </remarks>
public static class TextMatching
{
    /// <summary>The article stripped from an artist name before comparing.</summary>
    private const string LeadingArticle = "the ";

    /// <summary>
    /// Lower-cases, strips diacritics, turns <c>&amp;</c> into " and ", drops apostrophes and turns
    /// every other non-letter and non-digit into a single space.
    /// </summary>
    /// <param name="s">The text to normalise.</param>
    /// <returns>For example <c>beyonce and jay z</c> for <c>Beyoncé &amp; Jay-Z</c>.</returns>
    public static string Normalize(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        // Not Normalize(FormD): under InvariantGlobalization it leaves accented letters untouched.
        var folded = TextFolding.RemoveDiacritics(s);
        var builder = new StringBuilder(folded.Length);

        foreach (var character in folded)
        {
            switch (character)
            {
                case '&':
                    builder.Append(" and ");
                    break;
                case '\'':
                case '’':
                    break;
                default:
                    builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
                    break;
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>Normalises an artist name and drops a leading "the ".</summary>
    /// <param name="s">The artist name.</param>
    /// <returns>For example <c>beatles</c> for <c>The Beatles</c>.</returns>
    public static string NormalizeArtist(string s)
    {
        var normalized = Normalize(s);

        return normalized.StartsWith(LeadingArticle, StringComparison.Ordinal) ? normalized[LeadingArticle.Length..] : normalized;
    }

    /// <summary>
    /// The token-sort ratio of two strings: 1 minus the Levenshtein distance of their normalised,
    /// whitespace-separated tokens, each sorted, over the longer of the two.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>1.0 for two empty strings, 1.0 for the same words in a different order, 0.0 for no overlap.</returns>
    public static double Similarity(string a, string b)
    {
        var left = SortTokens(Normalize(a));
        var right = SortTokens(Normalize(b));
        var longest = Math.Max(left.Length, right.Length);

        return longest == 0 ? 1.0 : 1.0 - ((double)Levenshtein(left, right) / longest);
    }

    /// <summary>Sorts the whitespace-separated tokens of an already normalised string.</summary>
    private static string SortTokens(string normalized)
    {
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(tokens, StringComparer.Ordinal);

        return string.Join(' ', tokens);
    }

    /// <summary>Collapses runs of whitespace and trims.</summary>
    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The classic two-row Levenshtein distance.</summary>
    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
