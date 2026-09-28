using System.Globalization;
using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>What <see cref="SoulseekFilenameParser"/> reads out of one Soulseek remote path.</summary>
/// <param name="Parsed">The artist / title / album / track guesses and the version flags (MATCHING_ENGINE.md §6.1).</param>
/// <param name="Extension">Lower-case extension without the dot, when the file name ends in one.</param>
/// <param name="IsAudio">Whether the extension is an audio format a Soulseek result can carry.</param>
public sealed record SoulseekPath(ParsedName Parsed, string? Extension, bool IsAudio);

/// <summary>
/// Turns a raw Soulseek remote path — <c>@@saaje\Music\Daft Punk\Daft Punk - Random Access Memories
/// (2013)\08 - Get Lucky.flac</c> — into the artist / title / album / track guesses, version flags and
/// featured artists the decision engine scores (MATCHING_ENGINE.md §6.1). Pure code: slskd's attributes
/// go to <see cref="SoulseekQuality"/>, and nothing here touches the file system or the network.
/// </summary>
/// <remarks>
/// <para>
/// The rules are pinned by <c>tests/fixtures/filenames.json</c>, whose cases are real paths recorded
/// from the Soulseek network. Where the prose in the task and the golden file disagree the golden file
/// wins: notably, every bracket group left in the title once <see cref="VersionFlagParser"/> has taken
/// the version hints and the <c>feat.</c> clauses is removed as well, and
/// <see cref="ParsedName.HasUnexplainedBrackets"/> separately records that one of them was not
/// recognised (Sockseek's "bracket check").
/// </para>
/// <para>
/// Nothing is lower-cased or folded on the way out (rule 9): callers normalise when they compare, with
/// <see cref="TextMatching"/>.
/// </para>
/// </remarks>
public static class SoulseekFilenameParser
{
    /// <summary>One slash of either kind separates the segments of a remote path.</summary>
    private static readonly char[] PathSeparators = ['\\', '/'];

    /// <summary>Extensions that make a Soulseek result a file we could import.</summary>
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.Ordinal)
        {
            "mp3", "flac", "m4a", "aac", "ogg", "oga", "opus", "wav", "aif", "aiff", "ape", "wv", "wma", "alac",
        };

    /// <summary>Folders that only carry the track number or the format: skipped when reading the album and artist.</summary>
    private static readonly Regex SkipDiscFolderRegex = new(
        @"^(cd|disc|disk|volume|vol\.?)\s*\d+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Folders named after the format alone, e.g. <c>FLAC</c>, <c>24bit</c>.</summary>
    private static readonly Regex SkipFormatFolderRegex = new(
        @"^(flac|mp3|320|v0|v2|aac|m4a|alac|24bit|16bit|lossless)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A folder that classifies the release rather than naming it, e.g. <c>3. Live</c>.</summary>
    private static readonly Regex CategoryFolderRegex = new(
        @"^(\d+\.?\s*)?(live|albums?|singles?|eps?|compilations?|bootlegs?|demos?|remixes)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The folders that name nothing: share roots, "Music", "Various" and drive letters.</summary>
    private static readonly string[] GenericFolders =
    [
        "music", "music library", "musique", "mp3", "flac", "audio", "downloads", "complete",
        "shares", "share", "artists", "misc", "unknown", "_unknown_", "unknown album", "unknown artist",
        "various", "new folder",
    ];

    /// <summary>A leading track number: <c>08</c>, <c>A1</c>, <c>01-06</c>, <c>3.06</c>, <c>2x03</c>.</summary>
    private static readonly Regex LeadingTrackTokenRegex = new(
        @"^[A-D]?\d{1,4}([-.x]\d{1,3})?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A four-digit token that could be a year rather than a track number.</summary>
    private static readonly Regex YearOnlyTokenRegex = new(@"^\d{4}$", RegexOptions.CultureInvariant);

    /// <summary>Runs of digits inside one track-number token.</summary>
    private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.CultureInvariant);

    /// <summary>A segment separator: hyphen, en or em dash, surrounded by whitespace.</summary>
    private static readonly Regex DashSeparatorRegex = new(@"\s[-–—]\s", RegexOptions.CultureInvariant);

    /// <summary>A segment that carries nothing but a track number.</summary>
    private static readonly Regex DigitsOnlyRegex = new(@"^\d+$", RegexOptions.CultureInvariant);

    /// <summary>One bracketed group of a file name.</summary>
    private static readonly Regex BracketGroupRegex = new(@"\([^()]*\)|\[[^\[\]]*\]", RegexOptions.CultureInvariant);

    /// <summary>Whitespace, collapsed wherever a guess is assembled.</summary>
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>A year group: <c>(2013)</c>, <c>[2013]</c>, <c>[2009-04-17]</c>, <c>(2013-05-17)</c>.</summary>
    private static readonly Regex YearGroupRegex = new(
        @"^(?:\(\d{4}(?:[-.]\d{2}(?:[-.]\d{2})?)?\)|\[\d{4}(?:[-.]\d{2}(?:[-.]\d{2})?)?\])$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A release-type tag: <c>(album)</c>, <c>[EP]</c>. Version-bearing tags such as <c>(Live)</c> are
    /// deliberately not here: an album folder "Inni (Live)" has to reach the version parser, which is
    /// what puts <c>live</c> on <see cref="ParsedName.PathVersionFlags"/>.
    /// </summary>
    private static readonly Regex ReleaseTypeTagRegex = new(
        @"^(?:albums?|singles?|eps?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A leading year prefix of an album folder: <c>2013 - </c>, <c>2005. </c>, <c>[2005] </c>, <c>(2011) </c>.</summary>
    private static readonly Regex LeadingYearPrefixRegex = new(
        @"^(?:\d{4}\s*[-.]\s*|\[\d{4}\]\s*|\(\d{4}\)\s*)",
        RegexOptions.CultureInvariant);

    /// <summary>A leading date: <c>2005 11 24 </c>, <c>2005-11-24 </c> (never a bare year).</summary>
    private static readonly Regex LeadingDatePrefixRegex = new(
        @"^\d{4}([ .-]\d{2}){1,2}\s+",
        RegexOptions.CultureInvariant);

    /// <summary>A trailing <c> - Single</c> or <c> - EP</c> on an album folder.</summary>
    private static readonly Regex TrailingSingleEpRegex = new(
        @"\s*[-–—]\s*(?:single|ep)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A segment that is nothing but a <c>feat.</c> clause. The keyword must end at a space: a
    /// word boundary will not do, because <c>feat.</c> ends in a dot.</summary>
    private static readonly Regex FeatStartRegex = new(
        @"^\s*(?:feat\.?|ft\.?|featuring)\s",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The leading keyword of a <c>feat.</c> clause, dropped before the names are split.</summary>
    private static readonly Regex FeatKeywordRegex = new(
        @"^\s*(?:feat\.?|ft\.?|featuring)\s*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A <c>feat.</c> clause trailing the title or the artist.</summary>
    private static readonly Regex FeatClauseRegex = new(
        @"\s(?:feat\.?|ft\.?|featuring)\s+(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Separates the names of one <c>feat.</c> clause.</summary>
    private static readonly Regex FeatNameSeparatorRegex = new(
        @"\s*(?:&|,|\band\b)\s*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses one remote path, exactly as a Soulseek peer reports it (backslashes, share root and all).
    /// Never throws and never returns <c>null</c>: a path that says nothing yields
    /// <see cref="ParsedName.Empty"/> with <see cref="SoulseekPath.IsAudio"/> false.
    /// </summary>
    /// <param name="remotePath">The full remote path of the file.</param>
    public static SoulseekPath Parse(string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        var segments = remotePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return new SoulseekPath(ParsedName.Empty, null, false);
        }

        var file = segments[^1];
        var extension = ReadExtension(file, out var stem);
        var isAudio = extension is not null && AudioExtensions.Contains(extension);

        return new SoulseekPath(ParseName(NormalizeStem(stem), segments[..^1]), extension, isAudio);
    }

    /// <summary>The extension of the file name when it is 1–5 ASCII letters or digits, lower-cased.</summary>
    private static string? ReadExtension(string file, out string stem)
    {
        var dot = file.LastIndexOf('.');
        if (dot >= 0)
        {
            var candidate = file[(dot + 1)..];
            if (candidate.Length is >= 1 and <= 5 && candidate.All(char.IsAsciiLetterOrDigit))
            {
                stem = file[..dot];
                return candidate.ToLowerInvariant();
            }
        }

        stem = file;
        return null;
    }

    /// <summary>Rule 3: the separators a scene-style or underscore-style name hides its fields behind.</summary>
    private static string NormalizeStem(string stem)
    {
        var value = stem;

        if (!value.Contains(' ') && value.Count(character => character == '.') >= 2)
        {
            value = value.Replace('.', ' ');
        }

        if (!value.Contains(' '))
        {
            value = value.Replace('_', ' ');
        }

        if (value.Contains(' ') && value.Contains('_'))
        {
            value = value.Replace("_", " - ", StringComparison.Ordinal);
        }

        return Collapse(value);
    }

    /// <summary>Rules 4–8: everything the file name and the folders above it say.</summary>
    private static ParsedName ParseName(string stem, string[] folders)
    {
        var (remainder, trackNumber) = StripLeadingTrackNumbers(stem);
        var segments = SplitSegments(remainder, ref trackNumber);

        while (segments.Count >= 2 && IsPureVersionSegment(segments[^1]))
        {
            segments[^2] = string.Concat(segments[^2], " - ", segments[^1]);
            segments.RemoveAt(segments.Count - 1);
        }

        var (albumFolder, artistFolder) = FindFolders(folders);
        var albumFolderName = albumFolder is null ? null : CleanAlbumFolder(albumFolder);
        var pathFlags = CollectPathFlags(folders);

        string? artistSegment;
        string? titleSegment;
        string? albumSegment;

        if (segments.Count == 1)
        {
            (artistSegment, titleSegment, albumSegment) = (null, segments[0], null);
        }
        else if (segments.Count == 2)
        {
            (artistSegment, titleSegment, albumSegment) = (segments[0], segments[1], null);
        }
        else if (segments.Count >= 3)
        {
            // Three or more fields: the first segment is the artist and the second the album — unless the
            // first is the album folder's own name, in which case the order is reversed.
            var first = segments[0];
            var second = segments[1];
            titleSegment = segments[^1];

            if (albumFolderName is not null &&
                string.Equals(TextMatching.Normalize(first), TextMatching.Normalize(albumFolderName), StringComparison.Ordinal))
            {
                (albumSegment, artistSegment) = (first, second);
            }
            else
            {
                (artistSegment, albumSegment) = (first, second);
            }
        }
        else
        {
            (artistSegment, titleSegment, albumSegment) = (null, null, null);
        }

        // Rule 7: the artist comes from the folders only when the file name gave none.
        var artistFromAlbumFolderName = false;
        if (artistSegment is null && artistFolder is not null)
        {
            if (!IsGenericFolder(artistFolder))
            {
                artistSegment = artistFolder;
            }
            else if (albumFolderName is not null && TrySplitArtistAndAlbum(albumFolderName, out var artistName, out _))
            {
                artistSegment = artistName;
                artistFromAlbumFolderName = true;
            }
        }

        // Rule 8: likewise the album — unless the folder names nothing, like a share root — and rule 5's
        // three-or-more case wins when it found one.
        if (albumSegment is null && albumFolder is not null && albumFolderName is not null && !IsGenericFolder(albumFolder))
        {
            var cleaned = albumFolderName;

            if (TrySplitArtistAndAlbum(cleaned, out var folderArtist, out var folderAlbum) &&
                (artistFromAlbumFolderName ||
                 (artistSegment is not null && string.Equals(TextMatching.Normalize(folderArtist), TextMatching.Normalize(artistSegment), StringComparison.Ordinal))))
            {
                cleaned = folderAlbum;
            }

            cleaned = Collapse(TrailingSingleEpRegex.Replace(cleaned, string.Empty));

            var albumInfo = VersionFlagParser.Parse(cleaned);
            pathFlags |= albumInfo.Flags & VersionFlagNames.HardFlags;
            albumSegment = albumInfo.BaseTitle;
        }

        // Rule 6: the title, its flags and the featured artists.
        var featured = new List<string>();
        AddFeaturedArtists(featured, titleSegment);
        artistSegment = RemoveFeatClauses(artistSegment, featured);

        var flags = VersionFlags.None;
        IReadOnlyList<string> hints = [];
        string? title = null;

        if (titleSegment is not null)
        {
            var info = VersionFlagParser.Parse(titleSegment);
            flags = info.Flags;
            hints = info.Hints;
            title = Clean(RemoveBracketGroups(info.BaseTitle));
        }

        return new ParsedName(
            Clean(artistSegment),
            title,
            Clean(albumSegment),
            trackNumber,
            flags,
            hints,
            pathFlags,
            featured,
            HasUnexplainedBrackets(stem));
    }

    /// <summary>Rule 4: the track number the file name leads with, if any.</summary>
    private static (string Remainder, int? TrackNumber) StripLeadingTrackNumbers(string stem)
    {
        var working = stem.TrimStart();
        int? lastNumber = null;
        var stripped = false;

        while (true)
        {
            var match = LeadingTrackTokenRegex.Match(working);
            if (!match.Success || match.Index != 0)
            {
                break;
            }

            var token = match.Value;
            var rest = working[token.Length..];
            var separator = ReadTrackSeparator(rest);
            if (separator is null)
            {
                break;
            }

            // "1975 - Born to Run" is a year, not track 1975.
            if (!stripped && YearOnlyTokenRegex.IsMatch(token) && int.Parse(token, CultureInfo.InvariantCulture) is >= 1900 and <= 2099)
            {
                break;
            }

            lastNumber = LastNumberIn(token);
            stripped = true;
            working = rest[separator.Length..].TrimStart();
        }

        return (working, lastNumber is null ? null : TrackNumberFrom(lastNumber.Value));
    }

    /// <summary>The separator that ends a leading track-number token, or <c>null</c> when one does not follow.</summary>
    private static string? ReadTrackSeparator(string rest)
    {
        if (rest.StartsWith(" - ", StringComparison.Ordinal))
        {
            return " - ";
        }

        if (rest.Length == 0)
        {
            return null;
        }

        return rest[0] is '-' or '.' or ')' ? rest[..1] : char.IsWhiteSpace(rest[0]) ? rest[..1] : null;
    }

    /// <summary>A number of 100 or more is a disc plus a track: <c>0959</c> is track 59.</summary>
    private static int TrackNumberFrom(int value) => value >= 100 ? value % 100 : value;

    /// <summary>The last number inside one token, e.g. <c>6</c> for <c>01-06</c>.</summary>
    private static int LastNumberIn(string token)
    {
        var matches = NumberRegex.Matches(token);

        return matches.Count == 0 ? 0 : int.Parse(matches[matches.Count - 1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Rule 5: splits the remainder on dashes and takes out the track-number-only segments.</summary>
    private static List<string> SplitSegments(string remainder, ref int? trackNumber)
    {
        var segments = new List<string>();

        foreach (var piece in DashSeparatorRegex.Split(remainder))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (DigitsOnlyRegex.IsMatch(trimmed))
            {
                trackNumber ??= TrackNumberFrom(int.Parse(trimmed, CultureInfo.InvariantCulture));
                continue;
            }

            segments.Add(trimmed);
        }

        return segments;
    }

    /// <summary>True when a segment carries nothing but a version hint: <c>Radio Edit</c>, <c>(Live)</c>.</summary>
    private static bool IsPureVersionSegment(string segment)
    {
        var info = VersionFlagParser.Parse(string.Concat("x - ", segment));

        return info.Flags != VersionFlags.None && string.Equals(info.BaseTitle, "x", StringComparison.Ordinal);
    }

    /// <summary>The album folder and the artist folder: the nearest segments above the file that name something.</summary>
    private static (string? AlbumFolder, string? ArtistFolder) FindFolders(string[] folders)
    {
        var albumIndex = -1;
        for (var i = folders.Length - 1; i >= 0; i--)
        {
            if (!IsSkipFolder(folders[i]))
            {
                albumIndex = i;
                break;
            }
        }

        if (albumIndex < 0)
        {
            return (null, null);
        }

        for (var i = albumIndex - 1; i >= 0; i--)
        {
            if (!IsSkipFolder(folders[i]) && !IsCategoryFolder(folders[i]))
            {
                return (folders[albumIndex], folders[i]);
            }
        }

        return (folders[albumIndex], null);
    }

    /// <summary>The hard version flags the folders imply, e.g. a category folder <c>3. Live</c>.</summary>
    private static VersionFlags CollectPathFlags(string[] folders)
    {
        var flags = VersionFlags.None;

        foreach (var folder in folders)
        {
            var match = CategoryFolderRegex.Match(folder);
            if (match.Success && string.Equals(match.Groups[2].Value, "live", StringComparison.OrdinalIgnoreCase))
            {
                flags |= VersionFlags.Live;
            }
        }

        return flags;
    }

    private static bool IsSkipFolder(string segment) =>
        SkipDiscFolderRegex.IsMatch(segment) || SkipFormatFolderRegex.IsMatch(segment);

    private static bool IsCategoryFolder(string segment) => CategoryFolderRegex.IsMatch(segment);

    /// <summary>A folder that names nothing: a share root, "Music", "Downloads", a drive letter.</summary>
    private static bool IsGenericFolder(string segment)
    {
        if (segment.StartsWith("@@", StringComparison.Ordinal) || segment.EndsWith(':'))
        {
            return true;
        }

        foreach (var generic in GenericFolders)
        {
            if (string.Equals(segment, generic, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rule 8's structural cleaning of an album folder: leading year or date, year groups, format tags,
    /// release-type tags. The <c>X - Y</c> step needs the artist guess and is applied by the caller.
    /// </summary>
    private static string CleanAlbumFolder(string folder)
    {
        var value = LeadingYearPrefixRegex.Replace(folder, string.Empty);
        value = LeadingDatePrefixRegex.Replace(value, string.Empty);

        return Collapse(RemoveExplainedBracketGroups(value));
    }

    /// <summary>Splits <c>X - Y</c> at the first dash: <c>Daft Punk - Random Access Memories</c>.</summary>
    private static bool TrySplitArtistAndAlbum(string value, out string artist, out string album)
    {
        var separator = DashSeparatorRegex.Match(value);
        if (!separator.Success)
        {
            artist = value;
            album = value;
            return false;
        }

        artist = value[..separator.Index].Trim();
        album = value[(separator.Index + separator.Length)..].Trim();

        return artist.Length > 0 && album.Length > 0;
    }

    /// <summary>Removes the year groups, format tags and release-type tags an album folder carries.</summary>
    private static string RemoveExplainedBracketGroups(string value) =>
        BracketGroupRegex.Replace(value, match => IsExplainedBracketGroup(match.Value) ? " " : match.Value);

    /// <summary>Removes every bracket group a title still carries once the parser has taken its hints.</summary>
    private static string RemoveBracketGroups(string value) => BracketGroupRegex.Replace(value, " ");

    /// <summary>Whether a bracket group is one an album folder may simply drop.</summary>
    private static bool IsExplainedBracketGroup(string group)
    {
        var inner = group[1..^1];

        return YearGroupRegex.IsMatch(group) || IsFormatTag(inner) || IsReleaseTypeTag(inner);
    }

    /// <summary>Rule 6's format tags: a group whose every word names a format or a bitrate.</summary>
    private static bool IsFormatTag(string inner)
    {
        var words = inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        foreach (var word in words)
        {
            if (!FormatTagWords.Contains(word))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReleaseTypeTag(string inner) => ReleaseTypeTagRegex.IsMatch(inner.Trim());

    /// <summary>Whether the file name still carries a bracket group nothing explains.</summary>
    private static bool HasUnexplainedBrackets(string stem)
    {
        foreach (Match match in BracketGroupRegex.Matches(stem))
        {
            var inner = match.Value[1..^1];

            if (IsExplainedBracketGroup(match.Value) ||
                IsReleaseTypeTag(inner) ||
                FeatStartRegex.IsMatch(inner) ||
                IsVersionHintGroup(match.Value))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>Whether <see cref="VersionFlagParser"/> strips a bracket group as a version hint.</summary>
    private static bool IsVersionHintGroup(string group) =>
        string.Equals(VersionFlagParser.Parse(string.Concat("x ", group)).BaseTitle, "x", StringComparison.Ordinal);

    /// <summary>Rule 6: the names of a <c>feat.</c> clause, taken from the title and the artist alike.</summary>
    private static void AddFeaturedArtists(List<string> featured, string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return;
        }

        foreach (Match match in BracketGroupRegex.Matches(segment))
        {
            var inner = match.Value[1..^1];
            if (FeatStartRegex.IsMatch(inner))
            {
                AddFeaturedNames(featured, inner);
            }
        }

        var clause = FeatClauseRegex.Match(segment);
        if (clause.Success)
        {
            AddFeaturedNames(featured, clause.Groups[1].Value);
        }
    }

    /// <summary>The artist with its <c>feat.</c> clause taken out — <c>Daft Punk feat. X</c> is <c>Daft Punk</c>.</summary>
    private static string? RemoveFeatClauses(string? segment, List<string> featured)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return segment;
        }

        var value = BracketGroupRegex.Replace(segment, match =>
            FeatStartRegex.IsMatch(match.Value[1..^1]) ? " " : match.Value);

        var clause = FeatClauseRegex.Match(value);
        if (clause.Success)
        {
            AddFeaturedNames(featured, clause.Groups[1].Value);
            value = value[..clause.Index];
        }

        return Collapse(value);
    }

    private static void AddFeaturedNames(List<string> featured, string clause)
    {
        var body = FeatKeywordRegex.Replace(clause, string.Empty);

        foreach (var name in FeatNameSeparatorRegex.Split(body))
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                featured.Add(trimmed);
            }
        }
    }

    /// <summary>Rule 9: every guess is trimmed, and an empty guess is no guess.</summary>
    private static string? Clean(string? value)
    {
        var collapsed = Collapse(value ?? string.Empty);

        return collapsed.Length == 0 ? null : collapsed;
    }

    private static string Collapse(string value) => WhitespaceRegex.Replace(value, " ").Trim();

    /// <summary>Words that make a bracket group a format tag rather than part of a title.</summary>
    private static readonly HashSet<string> FormatTagWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "aac", "m4a", "alac", "ogg", "opus", "wav", "cd", "web", "vinyl", "lossless",
            "hq", "hd", "320", "320kbps", "256", "256kbps", "192", "192kbps", "kbps", "v0", "v2", "vbr",
            "cbr", "16bit", "24bit", "16-44", "16-44.1", "24-44.1", "24-48", "24-96", "24-192",
        };
}
