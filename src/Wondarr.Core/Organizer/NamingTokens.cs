// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Organizer/FileNameBuilder.cs, GPL-3.0
using System.Text.RegularExpressions;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The text helpers a naming template needs, ported from Lidarr's <c>FileNameBuilder</c> so a template
/// someone already knows from *arr renders the same here (LIBRARY_OUTPUT.md §7.1).
/// </summary>
internal static class NamingTokens
{
    /// <summary>A leading article, so it can be moved behind the name ("The Beatles" → "Beatles, The").</summary>
    private static readonly Regex TitlePrefixRegex =
        new(@"^(The|An|A) (.*?)((?: *\([^)]+\))*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Everything that is not a letter, a digit, a space or a hyphen.</summary>
    private static readonly Regex CleanTitleRemoveChars = new(@"[^\p{L}\p{Nd} -]", RegexOptions.Compiled);

    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly string[] IllegalCharacters = ["\\", "/", "<", ">", "?", "*", "|", "\""];

    private static readonly string[] ReplacementCharacters = ["+", "+", "", "", "!", "-", "", ""];

    /// <summary>Replaces the characters a file name may not contain, the way Lidarr's smart colon format does.</summary>
    /// <param name="name">A token value with the token's separator already applied.</param>
    /// <returns>The value with <c>\ / &lt; &gt; ? * | "</c> and <c>:</c> replaced, trimmed of leading dots and spaces.</returns>
    internal static string CleanFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // ": " becomes " - " so "Who Made Who: Live" still reads well; every other colon becomes "-".
        var result = name.Replace(": ", " - ", StringComparison.Ordinal).Replace(':', '-');

        for (var index = 0; index < IllegalCharacters.Length; index++)
        {
            result = result.Replace(IllegalCharacters[index], ReplacementCharacters[index], StringComparison.Ordinal);
        }

        return result.TrimStart(' ', '.').TrimEnd(' ');
    }

    /// <summary>Moves a leading article behind the name: "The Beatles" → "Beatles, The".</summary>
    internal static string TitleThe(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return TitlePrefixRegex.Replace(title, "$2, $1$3");
    }

    /// <summary>The first character of the NameThe form, upper-cased: "The Beatles" → "B".</summary>
    internal static string TitleFirstCharacter(string nameTheForm)
    {
        ArgumentNullException.ThrowIfNull(nameTheForm);

        return nameTheForm.Length == 0 ? string.Empty : char.ToUpperInvariant(nameTheForm[0]).ToString();
    }

    /// <summary>
    /// The "clean" form of a name or title: "&amp;" becomes "and", path separators become spaces, and
    /// everything that is not a letter, a digit, a space or a hyphen is dropped.
    /// </summary>
    internal static string CleanTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var result = title.Replace("&", "and", StringComparison.Ordinal).Replace('/', ' ').Replace('\\', ' ');
        result = CleanTitleRemoveChars.Replace(result, string.Empty);

        return CollapseWhitespaceRegex.Replace(result, " ").Trim();
    }
}
