using System.Text.RegularExpressions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// Compares a YouTube video's title with a song's title for the music video finder (ADR-0014): the
/// decorations a video adds ("(Official Video)") are stripped, and what is left must be the song's title at or
/// above the decision engine's title floor, with the same version (a live video is not a studio song's video).
/// </summary>
public static class MusicVideoTitleMatcher
{
    /// <summary>The decorations a video title may carry, between round or square brackets, in any case.</summary>
    private static readonly string[] Decorations =
    [
        "official video",
        "official music video",
        "official hd video",
        "video",
        "lyric video",
        "visualizer",
    ];

    /// <summary>The decorations that make a video not a music video: a lyric video or a visualizer.</summary>
    private static readonly string[] NotAMusicVideo = ["lyric video", "visualizer"];

    /// <summary>A bracketed group: round or square, with no bracket inside it.</summary>
    private static readonly Regex BracketRegex = new(
        @"\s*[\(\[]([^\(\)\[\]]*)[\)\]]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>A dash with a space on both sides, which separates an artist from a title.</summary>
    private static readonly Regex DashRegex = new(
        @"\s[-–—]\s",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The title without the decorations a video adds, and whether it names a lyric video or a visualizer.</summary>
    /// <param name="Title">The title without the decorations; any other bracket stays.</param>
    /// <param name="IsLyricOrVisualizer">Whether a decoration said the video is a lyric video or a visualizer.</param>
    public sealed record Stripped(string Title, bool IsLyricOrVisualizer);

    /// <summary>Strips the known decorations from a video title.</summary>
    /// <param name="videoTitle">The title as YouTube shows it.</param>
    public static Stripped Strip(string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(videoTitle);

        var lyricOrVisualizer = false;

        var stripped = BracketRegex.Replace(videoTitle, match =>
        {
            var content = match.Groups[1].Value.Trim();

            if (!Decorations.Contains(content, StringComparer.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            lyricOrVisualizer |= NotAMusicVideo.Contains(content, StringComparer.OrdinalIgnoreCase);

            return string.Empty;
        });

        return new Stripped(stripped.Trim(), lyricOrVisualizer);
    }

    /// <summary>
    /// How close a video's title is to the song's, from 0 to 1, after the decorations are stripped. A title that
    /// names the artist before a dash ("Radiohead - Creep") is compared by each of its parts, and the best counts.
    /// A video that carries a different version than the song (live, remix, acoustic and the like) scores 0.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static double Similarity(string songTitle, string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(videoTitle);

        var stripped = Strip(videoTitle).Title;

        if (stripped.Length == 0)
        {
            return 0;
        }

        var hard = VersionFlagNames.HardFlags;

        if ((VersionFlagParser.Parse(songTitle).Flags & hard) != (VersionFlagParser.Parse(stripped).Flags & hard))
        {
            return 0;
        }

        var songVariants = CandidateTitleMatcher.SongVariants(songTitle);
        var videoTexts = new List<string> { stripped };
        videoTexts.AddRange(DashRegex.Split(stripped).Select(part => part.Trim()).Where(part => part.Length > 0));

        var best = 0.0;

        foreach (var text in videoTexts)
        {
            foreach (var variant in songVariants)
            {
                best = Math.Max(best, TextMatching.Similarity(variant, text));
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a video's title is the song's: the decorations are stripped, it is not a lyric video or a
    /// visualizer, and it matches at or above <see cref="DecisionEngine.TitleFloor"/>.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static bool IsTitledAsTheSong(string songTitle, string videoTitle) =>
        !Strip(videoTitle).IsLyricOrVisualizer && Similarity(songTitle, videoTitle) >= DecisionEngine.TitleFloor;
}
