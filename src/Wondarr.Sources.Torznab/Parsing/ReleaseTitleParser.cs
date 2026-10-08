// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Parser/Parser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Only the music part is ported: the ReportAlbumTitleRegex family and the cleaning helpers around
// ParseAlbumTitle/ParseAlbumMatchCollection/ExtractYear. Dropped: TV/episode parsing, release
// groups and hashes, quality detection (Wondarr has ReleaseQualityParser) and Lidarr's logger.
// Extended: the bracket classes also accept '{' (indexers report "Artist - Album {Deluxe Edition}").

using System.Text.RegularExpressions;

namespace Wondarr.Sources.Torznab.Parsing;

/// <summary>Artist, album and year read from a release title, plus the version tags it carries.</summary>
/// <param name="Artist">The artist, with separators cleaned up.</param>
/// <param name="Album">The album ("Discography" for discography releases).</param>
/// <param name="Year">The release year, when the title carries a plausible one.</param>
/// <param name="Tags">Version hints such as WEB, CD, Vinyl, Deluxe or Remastered.</param>
public sealed record ParsedRelease(string Artist, string Album, int? Year, IReadOnlyList<string> Tags);

/// <summary>
/// Reads artist, album, year and version hints out of an indexer release title, the way Lidarr's
/// music parser does. Pure name parsing; returns <c>null</c> when no artist and album can be found.
/// </summary>
public static class ReleaseTitleParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex[] ReportAlbumTitleRegex =
    [
        // ruTracker - (Genre) [Source]? Artist - Discography
        new(@"^(?:\(.+?\))(?:\W*(?:\[(?<source>.+?)\]))?\W*(?<artist>.+?)(?: - )(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Discography with two years
        new(@"^(?<artist>.+?)(?: - )(?:.+?)?(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Discography with end year
        new(@"^(?<artist>.+?)(?: - )(?:.+?)?(?<discography>Discography|Discografia).+?(?<endyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist Discography with two years
        new(@"^(?<artist>.+?)\W*(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist Discography with end year
        new(@"^(?<artist>.+?)\W*(?<discography>Discography|Discografia).+?(?<endyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist Discography
        new(@"^(?<artist>.+?)\W*(?<discography>Discography|Discografia)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // ruTracker - (Genre) [Source]? Artist - Album - Year
        new(@"^(?:\(.+?\))(?:\W*(?:\[(?<source>.+?)\]))?\W*(?<artist>.+?)(?: - )(?<album>.+?)(?: - )(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album-Version-Source-Year
        // ex. Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM
        new(@"^(?<artist>.+?)[-](?<album>.+?)[-](?:[\(|\[]?)(?<version>.+?(?:Edition)?)(?:[\)|\]]?)[-](?<source>\d?CD|WEB).+?(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album-Source-Year
        // ex. Dani_Sbert-Togheter-WEB-2017-FURY
        new(@"^(?<artist>.+?)[-](?<album>.+?)[-](?<source>\d?CD|WEB).+?(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Album (Year) Strict
        new(@"^(?:(?<artist>.+?)(?: - )+)(?<album>.+?)\W*[\(\[\{][^\[\]\{\}]*?(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Album (Year)
        new(@"^(?:(?<artist>.+?)(?: - )+)(?<album>.+?)\W*[\(\[\{](?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Album - Year [something]
        new(@"^(?:(?<artist>.+?)(?: - )+)(?<album>.+?)\W*(?: - )(?<releaseyear>\d{4})\W*(?:\(|\[|\{)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Album [something] or Artist - Album (something) or {something}
        new(@"^(?:(?<artist>.+?)(?: - )+)(?<album>.+?)\W*(?:\(|\[|\{)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Album Year
        new(@"^(?:(?<artist>.+?)(?: - )+)(?<album>.+?)\W*(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album (Year) Strict
        // Hyphen no space between artist and album
        new(@"^(?:(?<artist>.+?)(?:-)+)(?<album>.+?)\W*[\(\[\{][^\[\]\{\}]*?(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album (Year)
        // Hyphen no space between artist and album
        new(@"^(?:(?<artist>.+?)(?:-)+)(?<album>.+?)\W*[\(\[\{](?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album [something] or Artist-Album (something)
        // Hyphen no space between artist and album
        new(@"^(?:(?<artist>.+?)(?:-)+)(?<album>.+?)\W*(?:\(|\[|\{)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album-something-Year
        new(@"^(?:(?<artist>.+?)(?:-)+)(?<album>.+?)(?:-.+?)(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist-Album Year
        // Hyphen no space between artist and album
        new(@"^(?:(?<artist>.+?)(?:-)+)(?:(?<album>.+?)(?:-)+)(?<releaseyear>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Artist - Year - Album
        // Hyphen with no or more spaces between artist/album/year
        new(@"^(?:(?<artist>.+?)(?:-))(?<releaseyear>\d{4})(?:-)(?<album>[^-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),

        // Hyphen with spaces between artist - year - album
        new(@"^(?:(?<artist>.+?)(?:\s?-\s?))(?<releaseyear>\d{4})(?:\s?-\s?)(?<album>[^-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),
    ];

    private static readonly Regex FileExtensionRegex = new(
        @"\.(torrent|flac|mp3|m4a|aac|ogg|oga|opus|wav|aiff|aif|ape|wv|zip|rar|7z|nzb|par2)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        MatchTimeout);

    private static readonly Regex CollapseSpacesRegex = new(@"\s+", RegexOptions.Compiled, MatchTimeout);

    // The version tags Wondarr looks for, in the order they are reported. The CD pattern also
    // catches "2CD" and friends.
    private static readonly (string Tag, Regex Pattern)[] TagPatterns =
    [
        ("WEB", new(@"\bWEB\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("CD", new(@"(?<![a-zA-Z])CD\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Vinyl", new(@"\bVINYL\b|\bLP\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Deluxe", new(@"\bDELUXE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Remastered", new(@"\bREMASTER(?:ED)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Limited", new(@"\bLIMITED\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Bonus", new(@"\bBONUS\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Explicit", new(@"\bEXPLICIT\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
        ("Live", new(@"\bLIVE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
    ];

    /// <summary>
    /// Parses artist, album, year and version hints from a release title.
    /// </summary>
    /// <param name="title">The release title as the indexer reported it.</param>
    /// <returns>The parsed release, or <c>null</c> when no artist and album can be found.</returns>
    public static ParsedRelease? Parse(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Lidarr rejects password-protected yEnc posts outright.
        if (title.Contains("password", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("yenc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var releaseTitle = FileExtensionRegex.Replace(title.Trim(), string.Empty);

        foreach (var regex in ReportAlbumTitleRegex)
        {
            Match match;
            try
            {
                match = regex.Match(releaseTitle);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            var result = ParseMatch(match, releaseTitle);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static ParsedRelease? ParseMatch(Match match, string releaseTitle)
    {
        var artist = CleanGroup(match.Groups["artist"]);
        var album = match.Groups["discography"].Success ? "Discography" : CleanGroup(match.Groups["album"]);

        if (artist.Length == 0 || album.Length == 0)
        {
            return null;
        }

        int? year = null;
        if (match.Groups["discography"].Success)
        {
            // A discography has no single release year; Lidarr leaves it unset too.
            year = null;
        }
        else
        {
            year = ExtractYear(match.Groups["releaseyear"]);
        }

        return new ParsedRelease(artist, album, year, ParseTags(releaseTitle));
    }

    private static List<string> ParseTags(string releaseTitle)
    {
        var tags = new List<string>();
        foreach (var (tag, pattern) in TagPatterns)
        {
            bool found;
            try
            {
                found = pattern.IsMatch(releaseTitle);
            }
            catch (RegexMatchTimeoutException)
            {
                found = false;
            }

            if (found)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static int? ExtractYear(Group yearGroup)
    {
        if (!yearGroup.Success || !int.TryParse(yearGroup.Value, out var year))
        {
            return null;
        }

        var currentYear = DateTime.UtcNow.Year;

        if (year < 1900 || year > currentYear + 1)
        {
            return null;
        }

        return year;
    }

    private static string CleanGroup(Group group)
    {
        if (!group.Success)
        {
            return string.Empty;
        }

        var value = group.Value.Replace('.', ' ').Replace('_', ' ');
        return CollapseSpacesRegex.Replace(value.Trim(), " ");
    }
}
