using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
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
    public static VersionFlags VersionFlagsOf(string title) => VersionOf(title, null, null, VersionFlags.None).Release;

    /// <summary>What a release name says about its versions: for the files, and for the path.</summary>
    /// <param name="Release">The flags every file in the release carries (<see cref="VersionFlagsOf"/>).</param>
    /// <param name="Path">Every hard flag the name's brackets and dash suffixes give, and <paramref name="Release"/>; the engine only penalises these.</param>
    public readonly record struct ReleaseVersion(VersionFlags Release, VersionFlags Path);

    /// <summary>
    /// <see cref="VersionFlagsOf"/> for a wanted song: a release that is the song's own album or the song's
    /// own single is named after it, and its version words are its title, not its version
    /// (<c>Paul McCartney - Live and Let Die</c> for "Live and Let Die", <c>Hole-Live_Through_This</c> for an
    /// album track, <c>Christina Aguilera - Stripped</c> for "Stripped"). The words of that album part count
    /// only as far as the song carries the flag itself (a live album's live song). Explicit tags still
    /// count: <c>Everlong (Live)</c> is live for the studio "Everlong" too, and so is a <c>-LIVE</c> field.
    /// </summary>
    /// <param name="title">The release name, as the indexer lists it.</param>
    /// <param name="songTitle">The wanted song's title, or null when none is known.</param>
    /// <param name="songAlbum">The wanted song's album, or null when none is known.</param>
    /// <param name="songFlags">The wanted song's own version flags.</param>
    /// <param name="targetAlbum">The album of the release the search targeted, or null: a second album the song is on, either one makes a release its own.</param>
    public static ReleaseVersion VersionOf(string title, string? songTitle, string? songAlbum, VersionFlags songFlags, string? targetAlbum = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        var name = ExtensionRegex.Replace(title.Trim(), string.Empty);
        var album = ReleaseTitleParser.Parse(name)?.Album;
        var targets = new[] { songAlbum, targetAlbum, songTitle is null ? null : VersionFlagParser.Parse(songTitle).BaseTitle }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();
        var albumIsOwn = album is not null && IsOwn(targets, album);

        // "Artist - Album" reads the album as a dash suffix, so an own album is cut out before the tags are read.
        var tagged = VersionFlagParser.Parse(albumIsOwn ? WithoutAlbum(name, album!) : name).Flags & VersionFlagNames.HardFlags;
        var flags = tagged & ReleaseFlags;

        if (album is not null)
        {
            flags |= VersionWords.FlagsOf(album) & ReleaseFlags & (albumIsOwn ? songFlags : AnyFlag);
        }

        // A scene name has no spaces: its fields past the artist's are the album, the source, the year.
        if (!name.Any(char.IsWhiteSpace))
        {
            var fields = name.Split('-');
            var ownFields = OwnFields(targets, fields);

            for (var index = 1; index < fields.Length; index++)
            {
                flags |= VersionWords.FlagsOf(fields[index]) & ReleaseFlags & (ownFields.Contains(index) ? songFlags : AnyFlag);
            }
        }

        return new ReleaseVersion(flags, tagged | flags);
    }

    /// <summary>Every flag, as the mask that lets the words of a part of the name through.</summary>
    private const VersionFlags AnyFlag = (VersionFlags)int.MaxValue;

    /// <summary>How close a part of a release name must be to the song's album or title to be it.</summary>
    private const double OwnThreshold = 0.85;

    private static bool IsOwn(List<string> targets, string text) =>
        targets.Any(target => TextMatching.Similarity(target, text) >= OwnThreshold);

    /// <summary>The indexes of the hyphen fields that, alone or in a run of up to four, spell the song's album or title.</summary>
    private static HashSet<int> OwnFields(List<string> targets, string[] fields)
    {
        var own = new HashSet<int>();

        if (targets.Count == 0)
        {
            return own;
        }

        for (var start = 0; start < fields.Length; start++)
        {
            for (var length = 1; length <= 4 && start + length <= fields.Length; length++)
            {
                if (IsOwn(targets, string.Join(' ', fields.Skip(start).Take(length))))
                {
                    own.UnionWith(Enumerable.Range(start, length));
                }
            }
        }

        return own;
    }

    /// <summary>
    /// The release name without its album. The album the parser read has its dots and underscores as spaces;
    /// the name is searched with the same swap, character for character, so the cut falls on the right text.
    /// The name unchanged when the album cannot be found in it.
    /// </summary>
    private static string WithoutAlbum(string name, string album)
    {
        var spaced = name.Replace('.', ' ').Replace('_', ' ');
        var at = spaced.IndexOf(album, StringComparison.OrdinalIgnoreCase);

        return at < 0 ? name : string.Concat(name.AsSpan(0, at), name.AsSpan(at + album.Length));
    }
}
