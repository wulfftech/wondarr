using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>One file inside a container (a torrent, an NZB, an unpacked usenet job).</summary>
/// <param name="Index">The file's position as the container numbers it (qBittorrent's file id).</param>
/// <param name="Path">The path inside the container, segments joined with <c>/</c>.</param>
/// <param name="Size">The file's size in bytes.</param>
public sealed record ContainerFile(int Index, string Path, long Size);

/// <summary>What the matcher knows about the wanted song.</summary>
/// <param name="Title">The song's title as stored (version hints such as "(Live)" included).</param>
/// <param name="VersionFlags">The song's own version flags.</param>
/// <param name="TrackNo">The track number on the release being searched, when known.</param>
/// <param name="DurationMs">The song's length, when known.</param>
/// <param name="QualityId">The quality the release claims, for the size window (1 = unknown: no window).</param>
/// <param name="SixteenBitStated">
/// Whether the release name explicitly claims 16-bit or a CD source. Any other FLAC release, a bare
/// "[FLAC]" or one that names a 24-bit form, may hold 24-bit files, so its size window reaches up to hi-res.
/// </param>
/// <param name="ReleaseArtist">
/// The artist the release name gives, when it names one. A scene file name leads with it
/// ("01-artist_name-track_title"), and the scene reading drops it to get at the title.
/// </param>
/// <param name="ReleaseGroup">
/// The scene group tag the release name ends with ("-FADA", "-pH"), when it has one. A scene file name often
/// ends with it, and the scene reading drops it, whatever its case.
/// </param>
/// <param name="ReleaseVersionFlags">
/// The hard version flags the release name carries ("Live.at.Wembley" is live, a "Remixes" album is remixes).
/// They count as every file's own flags, so a studio song never matches a file of a live release, and a
/// live song can.
/// </param>
/// <param name="SongArtist">
/// The wanted song's own artist, when known: a second guess at the artist a scene file name leads with, for
/// the release names whose artist cannot be read back (<c>A-ha-Hunting_High_and_Low</c>).
/// </param>
/// <param name="SongAlbum">
/// The wanted song's album, when known. A release named after it (or after the song) is that album: its
/// name's version words ("Live Through This", "Stripped") are its title, not its version. Used when the
/// candidate is built; the matcher itself reads <paramref name="ReleaseVersionFlags"/>. The title of the
/// song's album context, and <see langword="null"/> when that is a pseudo-album ("Singles"), which no release
/// is named after (<see cref="SongAlbumNames"/>).
/// </param>
/// <param name="TargetAlbum">
/// The album of the MusicBrainz release the search targeted, when the candidate came from a search. It is
/// the song's album as much as <paramref name="SongAlbum"/> is, and the only one a song filed under
/// "Singles" has: either one naming the release is enough.
/// </param>
public sealed record ContainerMatchRequest(
    string Title,
    VersionFlags VersionFlags,
    int? TrackNo,
    int? DurationMs,
    long QualityId,
    bool SixteenBitStated = false,
    string? ReleaseArtist = null,
    string? ReleaseGroup = null,
    VersionFlags ReleaseVersionFlags = VersionFlags.None,
    string? SongArtist = null,
    string? SongAlbum = null,
    string? TargetAlbum = null);

/// <summary>The file the matcher chose.</summary>
/// <param name="File">The file.</param>
/// <param name="Parsed">What its name says (artist, title, track number, flags).</param>
/// <param name="Extension">Its lower-case extension.</param>
/// <param name="TitleSimilarity">How close its title is to the song's, 0–1.</param>
/// <param name="TrackNoAgrees">Whether its track number is the one asked for (false when either is unknown).</param>
public sealed record ContainerMatch(
    ContainerFile File,
    ParsedName Parsed,
    string? Extension,
    double TitleSimilarity,
    bool TrackNoAgrees);

/// <summary>
/// Finds the wanted song's file inside a container (MATCHING_ENGINE
/// §6.4): an audio file whose title matches the song's, whose version flags agree with the song's,
/// and whose size fits the song's length at the release's quality; the track number breaks ties.
/// Two files that match equally well are no match — guessing would import the wrong song. A file whose
/// ordinary reading misses the title is read once more as a scene file name (<see cref="SceneFileName"/>),
/// under the same threshold and the same checks.
/// </summary>
public static partial class ContainerMatcher
{
    /// <summary>The title similarity a file needs (the normaliser's 0–1 scale).</summary>
    public const double TitleThreshold = 0.85;

    /// <summary>How close two similarities must be to count as a tie.</summary>
    private const double TieMargin = 0.02;

    /// <summary>Bytes per second at one kbps.</summary>
    private const double BytesPerSecondPerKbps = 1000.0 / 8;

    /// <summary>The smallest and largest plausible bitrates of each lossless group, in kbps.</summary>
    private const int LosslessMinimumKbps = 300;

    private const int LosslessMaximumKbps = 1_800;

    private const int HiResMaximumKbps = 6_000;

    private const int UncompressedMaximumKbps = 10_000;

    /// <summary>Finds the song's file, or <see langword="null"/> when no file, or more than one, fits.</summary>
    /// <param name="files">The container's files.</param>
    /// <param name="request">The wanted song.</param>
    public static ContainerMatch? Find(IReadOnlyList<ContainerFile> files, ContainerMatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(request);

        var song = VersionFlagParser.Parse(request.Title);
        var songTitle = song.BaseTitle;
        var songFlags = (request.VersionFlags | song.Flags) & VersionFlagNames.HardFlags;
        var window = SizeWindow(request.QualityId, request.DurationMs, request.SixteenBitStated);

        // For a FLAC release the name does not pin to 16-bit, the 16-bit window still breaks a tie: of two
        // copies of one song, the one that fits a CD-quality FLAC is the likelier file.
        var narrow = window is not null && !request.SixteenBitStated
            ? SizeWindow(request.QualityId, request.DurationMs, sixteenBitStated: true)
            : window;
        var claimedCodec = request.QualityId == 1
            ? null
            : SeedData.Qualities.FirstOrDefault(quality => quality.Id == request.QualityId)?.Codec;

        var matches = new List<(ContainerMatch Match, bool CodecAgrees, bool InNarrowWindow)>();

        foreach (var file in files)
        {
            var path = SoulseekFilenameParser.Parse(file.Path);

            if (!path.IsAudio)
            {
                continue;
            }

            if (window is { } bounds && (file.Size < bounds.Minimum || file.Size > bounds.Maximum))
            {
                continue;
            }

            var reading = ReadTitle(file, path.Parsed, songTitle, songFlags, request);

            if (reading is null)
            {
                continue;
            }

            var (parsed, similarity) = reading.Value;
            var trackAgrees = request.TrackNo is { } wanted && parsed.TrackNo == wanted;

            var inNarrow = narrow is not { } tight || (file.Size >= tight.Minimum && file.Size <= tight.Maximum);

            matches.Add((new ContainerMatch(file, parsed, path.Extension, similarity, trackAgrees), CodecAgrees(claimedCodec, path.Extension), inNarrow));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        // The track number first, then the file whose extension is the codec the release claims (a
        // "[FLAC]" release that also carries MP3 and Ogg copies, as archive.org's do), then the title.
        var ranked = matches
            .OrderByDescending(entry => entry.Match.TrackNoAgrees)
            .ThenByDescending(entry => entry.CodecAgrees)
            .ThenByDescending(entry => entry.Match.TitleSimilarity)
            .ToList();

        var best = ranked[0];

        var tied = ranked
            .TakeWhile(entry => entry.Match.TrackNoAgrees == best.Match.TrackNoAgrees
                && entry.CodecAgrees == best.CodecAgrees
                && best.Match.TitleSimilarity - entry.Match.TitleSimilarity < TieMargin)
            .ToList();

        if (tied.Count > 1)
        {
            // Equal matches are no match, unless exactly one of them fits the 16-bit window of a release
            // that never ruled 24-bit out.
            var tight = tied.Where(entry => entry.InNarrowWindow).ToList();

            return tight.Count == 1 ? tight[0].Match : null;
        }

        return best.Match;
    }

    /// <summary>
    /// The file's name as the song's, or null when it is not: first the ordinary reading, then, only when
    /// that misses the title, the scene reading. Each reading must reach <see cref="TitleThreshold"/> and
    /// carry the song's own hard version flags, so a live or remix file never passes for the studio song.
    /// </summary>
    private static (ParsedName Parsed, double Similarity)? ReadTitle(
        ContainerFile file,
        ParsedName parsed,
        string songTitle,
        VersionFlags songFlags,
        ContainerMatchRequest request)
    {
        // A live release makes every file in it live: the flags of the name count as the file's own.
        var releaseFlags = request.ReleaseVersionFlags & VersionFlagNames.HardFlags;
        var fileFlags = (parsed.VersionFlags | parsed.PathVersionFlags | releaseFlags) & VersionFlagNames.HardFlags;

        if (fileFlags == songFlags)
        {
            var title = string.IsNullOrWhiteSpace(parsed.Title)
                ? LeadingTrackNumber().Replace(System.IO.Path.GetFileNameWithoutExtension(file.Path), string.Empty)
                : parsed.Title;
            var similarity = TextMatching.Similarity(songTitle, title);

            if (similarity >= TitleThreshold)
            {
                return (WithReleaseFlags(parsed, releaseFlags), similarity);
            }
        }

        // "01-norah_jones-dont_know_why-ph.mp3": the ordinary parser sees one title, hyphens and all.
        if (SceneFileName.Read(file.Path, request.ReleaseArtist, request.SongArtist, request.ReleaseGroup) is not { } scene
            || ((parsed.VersionFlags | scene.Flags | parsed.PathVersionFlags | releaseFlags) & VersionFlagNames.HardFlags) != songFlags)
        {
            return null;
        }

        var sceneSimilarity = TextMatching.Similarity(songTitle, scene.Title);

        if (sceneSimilarity < TitleThreshold || CarriesOtherVersionWord(scene.Title, songTitle, songFlags))
        {
            return null;
        }

        return (WithReleaseFlags(
            parsed with
            {
                Title = scene.Title,
                VersionFlags = parsed.VersionFlags | scene.Flags,
                VersionHints = [.. parsed.VersionHints, .. scene.Hints],
            },
            releaseFlags), sceneSimilarity);
    }

    /// <summary>The file's parsed name with the release's version flags added, so the engine sees the file as the live take it is.</summary>
    private static ParsedName WithReleaseFlags(ParsedName parsed, VersionFlags releaseFlags) =>
        releaseFlags == VersionFlags.None ? parsed : parsed with { VersionFlags = parsed.VersionFlags | releaseFlags };

    /// <summary>
    /// Whether a scene title has a word the song's title has not that is a hard version word the song does not
    /// carry. A long title reaches the similarity threshold with one more word on it ("…_live", "…_remix"),
    /// and a scene name has no brackets or spaced dashes to tell the version parser so.
    /// </summary>
    private static bool CarriesOtherVersionWord(string sceneTitle, string songTitle, VersionFlags songFlags)
    {
        var songWords = TextMatching.Normalize(songTitle).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        return TextMatching.Normalize(sceneTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !songWords.Contains(word))
            .Any(word => (VersionWords.FlagsOf(word) & ~songFlags) != VersionFlags.None);
    }

    /// <summary>Whether a file's extension is the codec the release claims.</summary>
    private static bool CodecAgrees(string? codec, string? extension) => (codec, extension?.ToLowerInvariant()) switch
    {
        (null, _) or (_, null) => false,
        ("flac", "flac") or ("mp3", "mp3") or ("vorbis", "ogg") or ("opus", "opus") or ("wma", "wma") => true,
        ("aac", "m4a" or "aac") or ("alac", "m4a") or ("ape", "ape") or ("wavpack", "wv") => true,
        ("wav", "wav") or ("aiff", "aiff" or "aif") => true,
        _ => false,
    };

    /// <summary>"07 - ", "07. ", "1-07 " before a title: not part of it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:\d{1,2}[-.])?\d{1,3}\s*[-._)]?\s+", System.Text.RegularExpressions.RegexOptions.None, 100)]
    private static partial System.Text.RegularExpressions.Regex LeadingTrackNumber();

    /// <summary>
    /// The sizes a file of the song's length can have at the claimed quality, as loose as the engine's
    /// own size check (MATCHING_ENGINE §2): 70 % of the lowest bitrate to 150 % of the highest.
    /// A release that claims FLAC without an explicit 16-bit or CD claim may hold 24-bit files, so its upper
    /// bound is the hi-res one; an explicit 16-bit or CD claim keeps the 16-bit bound, and the lower bound
    /// never moves.
    /// </summary>
    private static (long Minimum, long Maximum)? SizeWindow(long qualityId, int? durationMs, bool sixteenBitStated)
    {
        if (durationMs is not { } milliseconds || milliseconds <= 0)
        {
            return null;
        }

        var quality = SeedData.Qualities.FirstOrDefault(row => row.Id == qualityId);

        if (quality is null || quality.Id == 1)
        {
            return null;
        }

        var unstatedFlac = !sixteenBitStated && quality.Codec == "flac" && quality.BitDepth == 16;

        (int Low, int High)? kbps = quality.Lossless
            ? (LosslessMinimumKbps, quality.Group switch
            {
                "Hi-res lossless" => HiResMaximumKbps,
                "Uncompressed" => UncompressedMaximumKbps,
                _ when unstatedFlac => HiResMaximumKbps,
                _ => LosslessMaximumKbps,
            })
            : quality.MinBitrate is { } low && quality.MaxBitrate is { } high ? (low, high) : null;

        if (kbps is not { } range)
        {
            return null;
        }

        var bytesPerKbps = milliseconds / 1000.0 * BytesPerSecondPerKbps;

        return ((long)(range.Low * bytesPerKbps * 0.7), (long)(range.High * bytesPerKbps * 1.5));
    }
}
