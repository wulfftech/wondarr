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
public sealed record ContainerMatchRequest(
    string Title,
    VersionFlags VersionFlags,
    int? TrackNo,
    int? DurationMs,
    long QualityId);

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
/// Finds the wanted song's file inside a container (DECISIONS build session 8 #3; MATCHING_ENGINE
/// §6.4): an audio file whose title matches the song's, whose version flags agree with the song's,
/// and whose size fits the song's length at the release's quality; the track number breaks ties.
/// Two files that match equally well are no match — guessing would import the wrong song.
/// </summary>
public static class ContainerMatcher
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
        var window = SizeWindow(request.QualityId, request.DurationMs);

        var matches = new List<ContainerMatch>();

        foreach (var file in files)
        {
            var path = SoulseekFilenameParser.Parse(file.Path);

            if (!path.IsAudio)
            {
                continue;
            }

            var parsed = path.Parsed;
            var fileFlags = (parsed.VersionFlags | parsed.PathVersionFlags) & VersionFlagNames.HardFlags;

            if (fileFlags != songFlags)
            {
                continue;
            }

            if (window is { } bounds && (file.Size < bounds.Minimum || file.Size > bounds.Maximum))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(parsed.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(file.Path)
                : parsed.Title;
            var similarity = TextMatching.Similarity(songTitle, title);

            if (similarity < TitleThreshold)
            {
                continue;
            }

            var trackAgrees = request.TrackNo is { } wanted && parsed.TrackNo == wanted;

            matches.Add(new ContainerMatch(file, parsed, path.Extension, similarity, trackAgrees));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var ranked = matches
            .OrderByDescending(match => match.TrackNoAgrees)
            .ThenByDescending(match => match.TitleSimilarity)
            .ToList();

        var best = ranked[0];

        if (ranked.Count > 1)
        {
            var next = ranked[1];

            if (next.TrackNoAgrees == best.TrackNoAgrees && best.TitleSimilarity - next.TitleSimilarity < TieMargin)
            {
                return null;
            }
        }

        return best;
    }

    /// <summary>
    /// The sizes a file of the song's length can have at the claimed quality, as loose as the engine's
    /// own size check (MATCHING_ENGINE §2): 70 % of the lowest bitrate to 150 % of the highest.
    /// </summary>
    private static (long Minimum, long Maximum)? SizeWindow(long qualityId, int? durationMs)
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

        (int Low, int High)? kbps = quality.Lossless
            ? (LosslessMinimumKbps, quality.Group switch
            {
                "Hi-res lossless" => HiResMaximumKbps,
                "Uncompressed" => UncompressedMaximumKbps,
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
