using System.Text.RegularExpressions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Parsing;

/// <summary>
/// What a release name says about the files in it, beyond its artist and album: the scene group that
/// posted it, and whether it is a live album, a remix album or a demo. Pure name reading.
/// </summary>
public static class ReleaseNameTags
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A file extension a release name may still carry (an NZB's or a torrent's name, an archive's).</summary>
    private static readonly Regex ExtensionRegex = new(
        @"\.(?:nzb|torrent|rar|zip|7z|par2|flac|mp3|m4a|aac|ogg|opus|wav|ape|wv)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>A year, which ends many a release name.</summary>
    private static readonly Regex YearRegex = new(@"^(?:19|20)\d{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>The words that name a format, a source, a bitrate or a state of the release, which are never a group.</summary>
    private static readonly Regex FormatWordRegex = new(
        @"^(?:flac|mp3|aac|m4a|ogg|opus|wav|alac|ape|wv|wma|web|\d*cd|cdda|cdr|cds|cdm|dvd|sacd|vinyl|lp|ep|single|sbd|v\d|\d{3}|\d+k|\d+kbps|\d{2}bit|lossless|lossy|hires|remaster|remastered|deluxe|retail|promo|proper|repack|readnfo|internal|va|ost)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// The scene group tag a release name ends with (<c>...-2002-FADA</c> is <c>FADA</c>), or null. A name
    /// is only scene-shaped with at least three hyphen-separated fields, and the last one is no tag when it
    /// is a year, a format or source word (<c>FLAC</c>, <c>2CD</c>, <c>WEB</c>, <c>V0</c>, <c>24BIT</c>), has a dot
    /// or a space in it, or is a version word (<c>LIVE</c>): <c>Prince-Purple_Rain-1984-FLAC</c>,
    /// <c>Adele-Hello</c> and <c>Artist-Album-Live</c> have no group.
    /// </summary>
    /// <param name="title">The release name, as the indexer lists it.</param>
    public static string? GroupOf(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var name = ExtensionRegex.Replace(title.Trim(), string.Empty);
        var fields = name.Split('-');

        if (fields.Length < 3)
        {
            return null;
        }

        var token = fields[^1].Trim();

        if (token.Length == 0
            || token.Any(char.IsWhiteSpace)
            || token.Contains('.', StringComparison.Ordinal)
            || YearRegex.IsMatch(token)
            || FormatWordRegex.IsMatch(token)
            || VersionWords.IsHardKeyword(token))
        {
            return null;
        }

        return token;
    }

    /// <summary>
    /// The flags that make a whole release a different recording of its songs. "Extended Play", "(Extended
    /// Edition)" and "Cover Girl" name an album, not a version of every track on it, so they are not here.
    /// </summary>
    private const VersionFlags ReleaseFlags =
        VersionFlags.Live |
        VersionFlags.Acoustic |
        VersionFlags.Remix |
        VersionFlags.Instrumental |
        VersionFlags.Acapella |
        VersionFlags.Karaoke |
        VersionFlags.Demo;

    /// <summary>
    /// The version flags a release name carries: the ones its brackets and dash suffixes give
    /// (<c>Album (Live) [FLAC]</c>), and the version words of its album and of its other hyphen-separated
    /// fields (<c>Foo.Fighters-Live.at.Wembley-2CD-FLAC-2008-GRP</c>, <c>Artist-Album-Live</c>). Only the
    /// flags that make every track a different recording count: live (unplugged, in concert, bootleg),
    /// acoustic, remix, instrumental, acapella, karaoke and demo. The artist field is left alone: a band
    /// called Live is not a live album.
    /// </summary>
    /// <param name="title">The release name, as the indexer lists it.</param>
    public static VersionFlags VersionFlagsOf(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var name = ExtensionRegex.Replace(title.Trim(), string.Empty);
        var flags = VersionFlagParser.Parse(name).Flags & ReleaseFlags;

        if (ReleaseTitleParser.Parse(name)?.Album is { } album)
        {
            flags |= VersionWords.FlagsOf(album);
        }

        // A scene name has no spaces: its fields past the artist's are the album, the source, the year.
        if (!name.Any(char.IsWhiteSpace))
        {
            foreach (var field in name.Split('-').Skip(1))
            {
                flags |= VersionWords.FlagsOf(field);
            }
        }

        return flags & ReleaseFlags;
    }
}
