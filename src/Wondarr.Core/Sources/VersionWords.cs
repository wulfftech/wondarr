using System.Text.RegularExpressions;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>
/// Reads version words (live, remix, demo …) out of text that is not a track title: a field of a scene
/// release name, a word of a file name. <see cref="VersionFlagParser"/> only looks at brackets and dash
/// suffixes, because the words of a main title are not hints ("Live and Let Die"); here the whole text is
/// the hint, as when a release name says <c>Live.at.Wembley</c> or a file name ends in <c>-live</c>.
/// </summary>
public static partial class VersionWords
{
    /// <summary>The plurals and aliases the keyword table lacks, as the singular form it knows.</summary>
    [GeneratedRegex(@"\b(?:(?<remix>remixes|remixed)|(?<instrumental>instrumentals)|(?<demo>demos)|(?<karaoke>karaokes)|(?<live>bootlegs?))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AliasWord();

    /// <summary>
    /// The hard version flags the words of <paramref name="text"/> carry: <c>Live.at.Wembley</c> is live,
    /// <c>Remixes</c> and <c>Demos</c> are remixes and demos, a <c>Bootleg</c> is live. Soft flags
    /// (remaster, explicit, clean) are left out, they do not make a different song.
    /// </summary>
    /// <param name="text">The words, with any separator between them.</param>
    public static VersionFlags FlagsOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = AliasWord().Replace(
            text.Replace('-', ' ').Replace('_', ' ').Replace('.', ' '),
            match => match.Groups["remix"].Success ? "remix"
                : match.Groups["instrumental"].Success ? "instrumental"
                : match.Groups["demo"].Success ? "demo"
                : match.Groups["karaoke"].Success ? "karaoke"
                : "live");

        // Read as the suffix of a title, which is the one place the version parser takes every word as a hint.
        return VersionFlagParser.Parse(string.Concat("x - ", words)).Flags & VersionFlagNames.HardFlags;
    }

    /// <summary>Whether the text is, or holds, a hard version word.</summary>
    /// <param name="text">A field or a word.</param>
    public static bool IsHardKeyword(string text) => FlagsOf(text) != VersionFlags.None;
}
