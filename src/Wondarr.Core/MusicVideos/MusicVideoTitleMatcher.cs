using System.Text.RegularExpressions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Verification;

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

    /// <summary>
    /// Words that, anywhere in a video's title, say it is not a music video: lyrics, a visualizer, or the audio
    /// over a picture. Matched as whole words, bracketed, after a dash, after a bar or bare.
    /// </summary>
    private static readonly Regex NotAVideoWords = new(
        @"\b(?:lyrics?|visuali[sz]er|audio)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Bracketed noise that says nothing about the song: "Music Video", "HD", a resolution, or a remaster credit
    /// a fan added ("1080p Remaster by aTunes"). Matched on the whole content of a bracket.
    /// </summary>
    private static readonly Regex NoiseBracket = new(
        @"^(?:music\s+video|full\s+hd|hd|hq|4k|uhd|(?:\d{3,4}p|4k|hd)\b.*|.*\bremaster(?:ed)?\b.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Words that say a video is another version of the song than the studio one, beyond the version flags the
    /// parser reads: live, a remix or mix, a cover, a reaction, vocals only and the like.
    /// </summary>
    private static readonly Regex OtherVersionWords = new(
        @"\b(?:live|remix|mix|cover|reaction|reacts?|instrumental|karaoke|acoustic|acapella|a\s+cappella|vocals?\s+only|isolated\s+vocals?|demo|slowed|reverb|sped\s+up|nightcore|8d|bass\s+boosted)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>An artist, then a quoted title: <c>Artist "Title"</c>.</summary>
    private static readonly Regex QuotedTitleRegex = new(
        @"^(?<artist>[^""“”«»]+?)\s+[""“«](?<title>[^""“”«»]+)[""”»]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

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

            if (!Decorations.Contains(content, StringComparer.OrdinalIgnoreCase) && !NoiseBracket.IsMatch(content))
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
        var parts = TitleParts(stripped);
        var videoFlags = VideoFlags(stripped, parts);

        if ((VersionFlagParser.Parse(songTitle).Flags & hard) != videoFlags)
        {
            return 0;
        }

        var songVariants = CandidateTitleMatcher.SongVariants(songTitle);
        var videoTexts = new List<string> { stripped };
        videoTexts.AddRange(parts);

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

    private static List<string> TitleParts(string stripped) =>
        DashRegex.Split(stripped).Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

    /// <summary>
    /// The hard version flags of a title. The version is checked on the whole title and on each part after a dash
    /// ("Teardrop - Live at Glastonbury"): a part is read as a version hint on its own, the way a disambiguation is.
    /// </summary>
    private static VersionFlags VideoFlags(string stripped, List<string> parts)
    {
        var flags = VersionFlagParser.Parse(stripped).Flags & VersionFlagNames.HardFlags;

        if (parts.Count > 1)
        {
            foreach (var part in parts)
            {
                flags |= VersionFlagParser.Parse(part, disambiguation: part).Flags & VersionFlagNames.HardFlags;
            }
        }

        return flags;
    }

    /// <summary>
    /// What a search result's title says about the song's artist and version. The artist may be named in the title
    /// (<c>Artist - Title</c>, <c>Title - Artist</c>, <c>Artist "Title"</c>, <c>Artist &amp; Other - Title</c>), as it
    /// is on a user upload whose channel is the uploader.
    /// </summary>
    /// <param name="ArtistInTitle">Whether the title names one of the song's artists in one of those forms.</param>
    /// <param name="TitleSimilarity">How close the title (the part after the artist, when named) is to the song's, 0 to 1.</param>
    /// <param name="IsTitledAsTheSong">Whether it is the song's title at or above the title floor, in the studio version, and not a lyric video.</param>
    /// <param name="IsOtherVersion">Whether the title says it is a live, remix, cover, vocals-only or similar version.</param>
    public sealed record Evaluation(bool ArtistInTitle, double TitleSimilarity, bool IsTitledAsTheSong, bool IsOtherVersion);

    /// <summary>Judges a search result's title against the song.</summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="songArtists">The song's artists.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static Evaluation Evaluate(string songTitle, IReadOnlyList<string> songArtists, string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(songArtists);
        ArgumentNullException.ThrowIfNull(videoTitle);

        var remaining = TitleAfterArtist(videoTitle, songArtists);
        var similarity = Similarity(songTitle, videoTitle);

        if (remaining is not null)
        {
            similarity = Math.Max(similarity, Similarity(songTitle, remaining));
        }

        var otherVersion = IsOtherVersion(songTitle, videoTitle);

        return new Evaluation(
            remaining is not null,
            similarity,
            !IsNotAMusicVideo(songTitle, videoTitle) && !otherVersion && similarity >= DecisionEngine.TitleFloor,
            otherVersion);
    }

    /// <summary>
    /// The part of a video's title left once the song's artist is taken off it, or <see langword="null"/> when the
    /// title does not name the artist in one of the known forms.
    /// </summary>
    /// <param name="videoTitle">The video's title.</param>
    /// <param name="songArtists">The song's artists.</param>
    public static string? TitleAfterArtist(string videoTitle, IReadOnlyList<string> songArtists)
    {
        ArgumentNullException.ThrowIfNull(videoTitle);
        ArgumentNullException.ThrowIfNull(songArtists);

        var stripped = Strip(videoTitle).Title;
        var parts = TitleParts(stripped);

        if (parts.Count >= 2)
        {
            if (ArtistEvidence.NamesAnyOf(parts[0], songArtists))
            {
                return string.Join(" - ", parts.Skip(1));
            }

            if (ArtistEvidence.NamesAnyOf(parts[^1], songArtists))
            {
                return string.Join(" - ", parts.Take(parts.Count - 1));
            }
        }

        var quoted = QuotedTitleRegex.Match(stripped);

        return quoted.Success && ArtistEvidence.NamesAnyOf(quoted.Groups["artist"].Value.Trim(), songArtists)
            ? quoted.Groups["title"].Value.Trim()
            : null;
    }

    /// <summary>
    /// Whether a video's title says it is another version than the song's: a hard version flag the song lacks, or a
    /// word such as live, mix, cover, reaction or vocals only that the song's own title does not carry.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static bool IsOtherVersion(string songTitle, string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(videoTitle);

        var stripped = Strip(videoTitle).Title;
        var flags = VideoFlags(stripped, TitleParts(stripped));

        // The song's own title may carry the word ("Live and Let Die"), which a part after a dash reads as a hint.
        var songFlags = (VersionFlagParser.Parse(songTitle).Flags | VersionFlagParser.Parse(songTitle, disambiguation: songTitle).Flags)
            & VersionFlagNames.HardFlags;

        if ((flags & ~songFlags) != VersionFlags.None)
        {
            return true;
        }

        var inSong = OtherVersionWords.Matches(songTitle).Select(match => match.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        return OtherVersionWords.Matches(stripped).Any(match => !inSong.Contains(match.Value.ToLowerInvariant()));
    }

    /// <summary>
    /// Whether a video's title is the song's: the decorations are stripped, it is not a lyric video or a
    /// visualizer, and it matches at or above <see cref="DecisionEngine.TitleFloor"/>.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static bool IsTitledAsTheSong(string songTitle, string videoTitle) =>
        !IsNotAMusicVideo(songTitle, videoTitle) && Similarity(songTitle, videoTitle) >= DecisionEngine.TitleFloor;

    /// <summary>
    /// Whether the title says the video is a lyric video, a visualizer or the audio over a picture: the words
    /// "lyric", "lyrics", "visualizer", "visualiser" or "audio" anywhere in it, unless the song's own title has them.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    public static bool IsNotAMusicVideo(string songTitle, string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(videoTitle);

        if (Strip(videoTitle).IsLyricOrVisualizer)
        {
            return true;
        }

        var inSong = NotAVideoWords.Matches(songTitle).Select(match => match.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        return NotAVideoWords.Matches(videoTitle).Any(match => !inSong.Contains(match.Value.ToLowerInvariant()));
    }
}
