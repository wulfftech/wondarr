using System.Text.RegularExpressions;

namespace Compilarr.Core.Metadata;

/// <summary>
/// Decides whether a title denotes a version of a song — live, remix, acoustic, radio edit, remaster,
/// karaoke, cover … — from the track title, the MusicBrainz recording disambiguation and the release
/// group's secondary types, and returns the bare title with those hints and <c>feat.</c> clauses removed.
/// </summary>
/// <remarks>
/// <para>
/// Only <em>segments</em> are examined, never the words of the main title ("Live and Let Die",
/// "Remix to Ignition" and "Mix Tape" carry no flags): every bracketed group, each qualifying
/// <c> - </c> suffix, the whole disambiguation string, and the release group's secondary types.
/// </para>
/// <para>
/// The rules and the golden cases are pinned by MATCHING_ENGINE.md §6.1–6.2 and
/// <c>tests/fixtures/version-flags.json</c>; the keyword table is data, so later phases add hints there
/// rather than in the algorithm.
/// </para>
/// </remarks>
public static class VersionFlagParser
{
    /// <summary>One bracketed group of the title: a better-flagged group is stripped, the others stay.</summary>
    private static readonly Regex BracketRegex = new(@"\([^()]*\)|\[[^\[\]]*\]", RegexOptions.CultureInvariant);

    /// <summary>A dash suffix separator: hyphen, en or em dash, surrounded by whitespace.</summary>
    private static readonly Regex DashSeparatorRegex = new(@"\s[-–—]\s", RegexOptions.CultureInvariant);

    /// <summary>A separator left dangling once a dash suffix has been removed.</summary>
    private static readonly Regex TrailingDashRegex = new(@"\s*[-–—]\s*$", RegexOptions.CultureInvariant);

    /// <summary>A <c>feat.</c> clause trailing the main title, outside any bracket.</summary>
    private static readonly Regex TrailingFeatRegex = new(
        @"\s+(?:feat\.?|ft\.?|featuring)\s+.*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>A segment that is nothing but a <c>feat.</c> clause (or a <c>with …</c> credit).</summary>
    private static readonly Regex FeatStartRegex = new(
        @"^\s*(?:feat\.?|ft\.?|featuring|with)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The start of a <c>feat.</c> clause inside a flagged segment; the rest is ignored.</summary>
    private static readonly Regex FeatClauseRegex = new(
        @"\b(?:feat\.?|ft\.?|featuring)\s",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A soundtrack attribution: <c>from "…"</c> — neutral, stripped from the base title.</summary>
    private static readonly Regex NeutralFromRegex = new(
        @"\bfrom\s+[""'“”‘’]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A <c>&lt;4-digit year&gt; mix</c> — neutral, never a remix.</summary>
    private static readonly Regex NeutralYearMixRegex = new(@"\b\d{4}\s+mix\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Splits a segment into words; anything that is not a letter or a digit separates.</summary>
    private static readonly Regex TokenSeparatorRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Words that make a following <c>mix</c> neutral rather than a remix.</summary>
    private static readonly string[] NeutralMixPrefixes =
        ["original", "radio", "extended", "mono", "stereo", "album", "single"];

    /// <summary>
    /// The keyword table: a run of words → the flags it produces. A rule with
    /// <see cref="VersionFlags.None"/> is a neutral keyword — it flags nothing but strips its segment
    /// from the base title. Where several rules match at the same position the longest one wins.
    /// </summary>
    private static readonly SegmentRule[] Rules =
    [
        // Live
        new(["live"], VersionFlags.Live),
        new(["in", "concert"], VersionFlags.Live),
        new(["unplugged"], VersionFlags.Live | VersionFlags.Acoustic),

        // Acoustic
        new(["acoustic"], VersionFlags.Acoustic),
        new(["stripped"], VersionFlags.Acoustic),

        // Instrumental
        new(["instrumental"], VersionFlags.Instrumental),

        // Acapella
        new(["a", "cappella"], VersionFlags.Acapella),
        new(["a", "capella"], VersionFlags.Acapella),
        new(["acapella"], VersionFlags.Acapella),
        new(["acappella"], VersionFlags.Acapella),

        // Karaoke
        new(["karaoke"], VersionFlags.Karaoke),
        new(["backing", "track"], VersionFlags.Karaoke),

        // Cover
        new(["cover"], VersionFlags.Cover),
        new(["covered", "by"], VersionFlags.Cover),
        new(["originally", "performed", "by"], VersionFlags.Cover),
        new(["in", "the", "style", "of"], VersionFlags.Cover),
        new(["made", "famous", "by"], VersionFlags.Cover),
        new(["tribute"], VersionFlags.Cover),

        // Radio edit
        new(["radio", "edit"], VersionFlags.RadioEdit),
        new(["radio", "version"], VersionFlags.RadioEdit),
        new(["radio", "mix"], VersionFlags.RadioEdit),

        // Extended
        new(["extended"], VersionFlags.Extended),

        // Edit (intro/outro are DJ edits, and only in a disambiguation)
        new(["edit"], VersionFlags.Edit),
        new(["intro"], VersionFlags.Edit, DisambiguationOnly: true),
        new(["outro"], VersionFlags.Edit, DisambiguationOnly: true),

        // Remix; words ending in "mix" are handled by IsNeutralMix and the mix-suffix pass in Classify
        new(["remix"], VersionFlags.Remix),
        new(["rmx"], VersionFlags.Remix),
        new(["dub"], VersionFlags.Remix),

        // Remaster
        new(["remaster"], VersionFlags.Remaster),
        new(["remastered"], VersionFlags.Remaster),

        // Parental advisory
        new(["explicit"], VersionFlags.Explicit),
        new(["dirty"], VersionFlags.Explicit),
        new(["clean"], VersionFlags.Clean),
        new(["censored"], VersionFlags.Clean),

        // Demo
        new(["demo"], VersionFlags.Demo),

        // Slowed / sped up family
        new(["slowed"], VersionFlags.Slowed),
        new(["sped", "up"], VersionFlags.Slowed),
        new(["speed", "up"], VersionFlags.Slowed),
        new(["nightcore"], VersionFlags.Slowed),
        new(["reverb"], VersionFlags.Slowed),

        new(["8d"], VersionFlags.EightD),
        new(["bass", "boosted"], VersionFlags.BassBoosted),
        new(["bassboosted"], VersionFlags.BassBoosted),

        // Neutral: no flag, but stripped from the base title
        new(["original", "mix"], VersionFlags.None),
        new(["original", "version"], VersionFlags.None),
        new(["album", "version"], VersionFlags.None),
        new(["album", "mix"], VersionFlags.None),
        new(["single", "version"], VersionFlags.None),
        new(["single", "mix"], VersionFlags.None),
        new(["mono", "version"], VersionFlags.None),
        new(["mono", "mix"], VersionFlags.None),
        new(["stereo", "version"], VersionFlags.None),
        new(["stereo", "mix"], VersionFlags.None),
        new(["mono"], VersionFlags.None),
        new(["stereo"], VersionFlags.None),
        new(["bonus", "track"], VersionFlags.None),
    ];

    /// <summary>
    /// The flags <paramref name="title"/> carries, the title with those hints and <c>feat.</c> clauses
    /// removed, and the raw segments that produced them.
    /// </summary>
    /// <param name="title">The track title, as tagged.</param>
    /// <param name="disambiguation">The MusicBrainz recording disambiguation, if any; every word counts there.</param>
    /// <param name="releaseGroupSecondaryTypes">The release group's secondary types, if known.</param>
    public static VersionInfo Parse(
        string title,
        string? disambiguation = null,
        IReadOnlyCollection<string>? releaseGroupSecondaryTypes = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        var flags = VersionFlags.None;
        var hints = new List<string>();

        // Bracketed groups first, so a " - " inside a kept bracket cannot split the title.
        var working = BracketRegex.Replace(title, match =>
        {
            var segment = Classify(match.Value[1..^1], isDisambiguation: false);
            if (!segment.IsVersionSegment)
            {
                return match.Value;
            }

            flags |= segment.Flags;
            hints.Add(match.Value.Trim());
            return " ";
        });

        // Dash suffixes, from the right: a suffix only counts when it looks like a version segment.
        while (true)
        {
            var separators = DashSeparatorRegex.Matches(working);
            if (separators.Count == 0)
            {
                break;
            }

            var separator = separators[separators.Count - 1];
            var suffix = working[(separator.Index + separator.Length)..];
            var segment = Classify(suffix, isDisambiguation: false);
            if (!segment.IsVersionSegment)
            {
                break;
            }

            flags |= segment.Flags;
            hints.Add(suffix.Trim());
            working = working[..separator.Index];
        }

        var feat = TrailingFeatRegex.Match(working);
        if (feat.Success)
        {
            hints.Add(feat.Value.Trim());
            working = working[..feat.Index];
        }

        if (!string.IsNullOrWhiteSpace(disambiguation))
        {
            var segment = Classify(disambiguation, isDisambiguation: true);
            flags |= segment.Flags;
            if (segment.Flags != VersionFlags.None)
            {
                hints.Add(disambiguation.Trim());
            }
        }

        if (releaseGroupSecondaryTypes is not null)
        {
            foreach (var secondaryType in releaseGroupSecondaryTypes)
            {
                var mapped = MapSecondaryType(secondaryType);
                flags |= mapped;
                if (mapped != VersionFlags.None)
                {
                    hints.Add(secondaryType);
                }
            }
        }

        return new VersionInfo(flags, NormalizeTitle(working), hints);
    }

    /// <summary>The outcome of parsing one title: its flags, its bare title and the segments that were seen.</summary>
    /// <param name="Flags">The version flags the title carries.</param>
    /// <param name="BaseTitle">The title with every version, neutral and feat. segment removed.</param>
    /// <param name="Hints">The raw segments that produced a flag or were stripped, for debugging and the UI.</param>
    public sealed record VersionInfo(VersionFlags Flags, string BaseTitle, IReadOnlyList<string> Hints);

    /// <summary>Classifies one segment: the flags it yields, and whether it is a version segment at all.</summary>
    private static SegmentResult Classify(string segment, bool isDisambiguation)
    {
        if (FeatStartRegex.IsMatch(segment))
        {
            return new SegmentResult(VersionFlags.None, IsVersionSegment: true);
        }

        var flags = VersionFlags.None;
        var isVersionSegment = false;

        // Inside a flagged segment the feat. part is ignored: "Radio Edit - feat. X" is still a radio edit.
        var text = segment;
        var featClause = FeatClauseRegex.Match(segment);
        if (featClause.Success)
        {
            text = segment[..featClause.Index];
        }

        var tokens = Tokenize(text);
        var consumed = new bool[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            var rule = MatchRule(tokens, i, isDisambiguation);
            if (rule is null)
            {
                continue;
            }

            flags |= rule.Value.Flags;
            isVersionSegment = true;

            for (var j = i; j < i + rule.Value.Tokens.Length; j++)
            {
                consumed[j] = true;
            }

            i += rule.Value.Tokens.Length - 1;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (consumed[i] || !tokens[i].EndsWith("mix", StringComparison.OrdinalIgnoreCase) || IsNeutralMix(tokens, i))
            {
                continue;
            }

            flags |= VersionFlags.Remix;
            isVersionSegment = true;
        }

        if (NeutralFromRegex.IsMatch(segment) || NeutralYearMixRegex.IsMatch(segment))
        {
            isVersionSegment = true;
        }

        return new SegmentResult(flags, isVersionSegment);
    }

    /// <summary>The longest rule matching at <paramref name="index"/>, or <c>null</c>.</summary>
    private static SegmentRule? MatchRule(string[] tokens, int index, bool isDisambiguation)
    {
        SegmentRule? best = null;

        foreach (var rule in Rules)
        {
            if ((rule.DisambiguationOnly && !isDisambiguation) ||
                index + rule.Tokens.Length > tokens.Length ||
                (best is not null && rule.Tokens.Length <= best.Value.Tokens.Length))
            {
                continue;
            }

            var matches = true;
            for (var j = 0; j < rule.Tokens.Length && matches; j++)
            {
                matches = string.Equals(tokens[index + j], rule.Tokens[j], StringComparison.OrdinalIgnoreCase);
            }

            if (matches)
            {
                best = rule;
            }
        }

        return best;
    }

    /// <summary>True when a word ending in "mix" is a neutral mix rather than a remix.</summary>
    private static bool IsNeutralMix(string[] tokens, int index)
    {
        if (index == 0)
        {
            return false;
        }

        var previous = tokens[index - 1];
        if (previous.Length == 4 && previous.All(char.IsAsciiDigit))
        {
            return true;
        }

        foreach (var prefix in NeutralMixPrefixes)
        {
            if (string.Equals(previous, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps a release group secondary type onto a flag; unmapped types yield <see cref="VersionFlags.None"/>.</summary>
    private static VersionFlags MapSecondaryType(string? secondaryType)
    {
        if (string.IsNullOrWhiteSpace(secondaryType))
        {
            return VersionFlags.None;
        }

        var type = secondaryType.Trim();

        if (string.Equals(type, "Live", StringComparison.OrdinalIgnoreCase))
        {
            return VersionFlags.Live;
        }

        if (string.Equals(type, "Remix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "DJ-mix", StringComparison.OrdinalIgnoreCase))
        {
            return VersionFlags.Remix;
        }

        if (string.Equals(type, "Demo", StringComparison.OrdinalIgnoreCase))
        {
            return VersionFlags.Demo;
        }

        return VersionFlags.None;
    }

    private static string[] Tokenize(string text)
    {
        var parts = TokenSeparatorRegex.Split(text);
        var tokens = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                tokens.Add(part);
            }
        }

        return tokens.ToArray();
    }

    /// <summary>Collapses whitespace and drops a separator left dangling by a removed suffix.</summary>
    private static string NormalizeTitle(string value)
    {
        var collapsed = WhitespaceRegex.Replace(value, " ").Trim();
        return TrailingDashRegex.Replace(collapsed, string.Empty).Trim();
    }

    /// <summary>One keyword rule of the table.</summary>
    /// <param name="Tokens">The words, matched whole and in order.</param>
    /// <param name="Flags">The flags the rule yields; <see cref="VersionFlags.None"/> marks a neutral keyword.</param>
    /// <param name="DisambiguationOnly">True for rules that only apply inside a disambiguation.</param>
    private readonly record struct SegmentRule(string[] Tokens, VersionFlags Flags, bool DisambiguationOnly = false);

    /// <summary>The flags one segment yields and whether it was recognised as a version segment.</summary>
    private readonly record struct SegmentResult(VersionFlags Flags, bool IsVersionSegment);
}
