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
    /// <summary>The version flags that tell a studio video from another version of the song: edits are not one.</summary>
    /// <summary>The shortest name that may be found inside another when a channel is matched to an artist.</summary>
    private const int MinimumNameLength = 3;

    private const VersionFlags VideoHardFlags = VersionFlagNames.HardFlags & ~(VersionFlags.Edit | VersionFlags.RadioEdit);

    /// <summary>The words a channel name ends with that say nothing about who it is.</summary>
    private static readonly string[] ChannelSuffixes = ["officialchannel", "official", "vevo", "music", "tv", "channel"];

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
        @"^(?:music\s+video|full\s+hd|hd|hq|4k|uhd|\d{3,4}p|(?:(?:19|20)\d{2}\s+)?remaster(?:ed)?(?:\s+(?:19|20)\d{2})?(?:\s+by\s+[^\[\]()]+)?)"
        + @"(?:[\s,&/\-]+(?:music\s+video|full\s+hd|hd|hq|4k|uhd|\d{3,4}p|(?:(?:19|20)\d{2}\s+)?remaster(?:ed)?(?:\s+(?:19|20)\d{2})?(?:\s+by\s+[^\[\]()]+)?))*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Words that say a video is another version of the song than the studio one, beyond the version flags the
    /// parser reads: live, a remix, a cover, a reaction, vocals only and the like. A bare "mix" is left to the parser,
    /// which reads a named mix ("Rah Mix") as a remix and "Radio Mix" or "Original Mix" as neutral.
    /// </summary>
    private static readonly Regex OtherVersionWords = new(
        @"\b(?:live|remix|cover(?:ed)?|reaction|reacts?|instrumental|karaoke|acoustic|acapella|a\s+cappella|vocals?\s+only|isolated\s+vocals?|demo|slowed|reverb|sped\s+up|nightcore|8d|bass\s+boosted)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The words that make an upload audio over a picture, not a music video: official audio, audio, lyrics, a lyric
    /// video, a visualizer or a static video. They count in a bracket or after a separator ("Title // Lyrics").
    /// </summary>
    private static readonly Regex AudioOnlyWords = new(
        @"(?:[\(\[][^\)\]]*|(?:\s[-–—|*•·~]|\s//|:)\s*(?:official\s+)?)\b(?<word>official\s+audio|audio|lyrics?(?:\s+video)?|visuali[sz]er|static\s+video)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The edit and single-version markers a song's title may carry, which a video does not repeat ("single edit",
    /// "radio edit", "7\" version"): soft for music videos, whatever the audio decision engine says.
    /// </summary>
    private static readonly Regex SoftVersion = new(
        @"^(?:(?:single|album|radio|7\s*(?:[""”″]|''|[- ]?inch)?|12\s*(?:[""”″]|''|[- ]?inch))\s+(?:edit|version|mix)|edit)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>A dash and a soft version marker at the end of a title: <c>Joyride - Single Edit</c>.</summary>
    private static readonly Regex SoftVersionDashSuffix = new(
        @"\s[-–—]\s(?:(?:single|album|radio|7\s*(?:[""”″]|''|[- ]?inch)?|12\s*(?:[""”″]|''|[- ]?inch))\s+(?:edit|version|mix)|edit)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>An official-video decoration with remaster or resolution words after it: <c>Official Video Remastered 4K</c>.</summary>
    private static readonly Regex OfficialVideoBracket = new(
        @"^official\s+(?:music\s+)?(?:hd\s+)?video(?:[\s,&/\-]+(?:remaster(?:ed)?|hd|hq|4k|uhd|\d{3,4}p|(?:19|20)\d{2}))*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>A bare year in a bracket: <c>(1991)</c>.</summary>
    private static readonly Regex YearBracket = new(
        @"^(?:19|20)\d{2}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// What separates an artist from a title: a dash, <c>*</c>, a bar, a bullet, <c>//</c> or <c>~</c> with
    /// whitespace round it, or a colon and whitespace after it.
    /// </summary>
    private static readonly Regex ArtistSeparator = new(
        @"\s+(?:[-–—*|•·~]|//)\s+|\s*:\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Noise a fan leaves at the end of a title after the song's name: <c>1991 HQ</c>.</summary>
    private static readonly Regex TrailingNoise = new(
        @"(?:\s+(?:hq|hd|4k|uhd|\d{3,4}p|(?:19|20)\d{2}))+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>A trailing "(feat. …)", "[ft. …]" or "featuring …" clause.</summary>
    private static readonly Regex FeaturingTail = new(
        @"\s*[\(\[]?\s*\b(?:feat|ft|featuring)\b\.?.*$",
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

    private static bool IsNoise(string content) =>
        NoiseBracket.IsMatch(content)
        && !OtherVersionWords.IsMatch(content)
        && (VersionFlagParser.Parse(content, disambiguation: content).Flags & VersionFlagNames.HardFlags) == VersionFlags.None;

    /// <summary>Whether a bracket's content is only an edit or single-version marker, which a music video ignores.</summary>
    private static bool IsSoftVersion(string content)
    {
        if (SoftVersion.IsMatch(content))
        {
            return true;
        }

        var flags = VersionFlagParser.Parse(content, disambiguation: content).Flags;

        return flags != VersionFlags.None && (flags & ~(VersionFlags.Edit | VersionFlags.RadioEdit)) == VersionFlags.None;
    }

    /// <summary>
    /// A song's title as a music video is searched for and compared: without its edit and single-version markers
    /// ("Joyride (single edit)" is "Joyride"), which a video's own title rarely repeats. A title that is only such a
    /// marker is left as it is.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    public static string SearchTitle(string songTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);

        var bare = BracketRegex.Replace(songTitle, match => IsSoftVersion(match.Groups[1].Value.Trim()) ? string.Empty : match.Value);
        bare = SoftVersionDashSuffix.Replace(bare, string.Empty).Trim();

        return bare.Length > 0 ? bare : songTitle;
    }

    /// <summary>Strips the known decorations from a video title.</summary>
    /// <param name="videoTitle">The title as YouTube shows it.</param>
    public static Stripped Strip(string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(videoTitle);

        var lyricOrVisualizer = false;

        var stripped = BracketRegex.Replace(videoTitle, match =>
        {
            var content = match.Groups[1].Value.Trim();

            // A bracket goes only when all of it is noise, and never one that names a version. An edit or a
            // single version is not a version for a music video (Roxette "Joyride (single edit)").
            if (!Decorations.Contains(content, StringComparer.OrdinalIgnoreCase)
                && !IsNoise(content)
                && !OfficialVideoBracket.IsMatch(content)
                && !IsSoftVersion(content)
                && !YearBracket.IsMatch(content))
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

        var hard = VideoHardFlags;
        songTitle = SearchTitle(songTitle);
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
        var flags = VersionFlagParser.Parse(stripped).Flags & VideoHardFlags;

        if (parts.Count > 1)
        {
            foreach (var part in parts)
            {
                flags |= VersionFlagParser.Parse(part, disambiguation: part).Flags & VideoHardFlags;
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

        var otherVersion = IsOtherVersion(songTitle, videoTitle, songArtists);

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

        var stripped = Whitespace.Replace(Strip(videoTitle).Title, " ").Trim();
        var separators = ArtistSeparator.Matches(stripped);

        // The artist is whatever stands before one of the separators, so an artist whose own name holds a separator
        // ("A * B") still parses: the later separator is tried next.
        foreach (Match separator in separators)
        {
            if (ArtistEvidence.NamesAnyOf(stripped[..separator.Index], songArtists))
            {
                return Bare(stripped[(separator.Index + separator.Length)..]);
            }
        }

        // "Title - Artist": the artist after the last separator. "Title (feat. Artist)" credits a guest, it does not
        // name the song's artist as the video's own.
        for (var index = separators.Count - 1; index >= 0; index--)
        {
            var separator = separators[index];
            var last = FeaturingTail.Replace(stripped[(separator.Index + separator.Length)..], string.Empty).Trim();

            if (last.Length > 0 && ArtistEvidence.NamesAnyOf(last, songArtists))
            {
                return Bare(stripped[..separator.Index]);
            }
        }

        var quoted = QuotedTitleRegex.Match(stripped);

        return quoted.Success && ArtistEvidence.NamesAnyOf(quoted.Groups["artist"].Value.Trim(), songArtists)
            ? quoted.Groups["title"].Value.Trim()
            : null;
    }

    /// <summary>A title without the noise a fan leaves after it ("Joyride 1991 HQ").</summary>
    private static string Bare(string title) => TrailingNoise.Replace(title, string.Empty).Trim();

    /// <summary>
    /// Whether a video's title says it is another version than the song's: a hard version flag the song lacks, or a
    /// word such as live, remix, cover, reaction or vocals only that the song's own title does not carry. When the
    /// song's artists are given and the title names one, only the part after the artist is read, and a word that is in an
    /// artist's name ("Live", "Little Mix") is not a version.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's title, decorations and all.</param>
    /// <param name="songArtists">The song's artists, or <see langword="null"/> to read the whole title.</param>
    public static bool IsOtherVersion(string songTitle, string videoTitle, IReadOnlyList<string>? songArtists = null)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(videoTitle);

        var stripped = songArtists is null
            ? Strip(videoTitle).Title
            : Strip(TitleAfterArtist(videoTitle, songArtists) ?? videoTitle).Title;
        var flags = VideoFlags(stripped, TitleParts(stripped));

        // The song's own title may carry the word ("Live and Let Die"), which a part after a dash reads as a hint.
        var songFlags = (VersionFlagParser.Parse(songTitle).Flags | VersionFlagParser.Parse(songTitle, disambiguation: songTitle).Flags)
            & VideoHardFlags;

        if ((flags & ~songFlags) != VersionFlags.None)
        {
            return true;
        }

        var inSong = OtherVersionWords.Matches(songTitle).Select(match => match.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        foreach (var artist in songArtists ?? [])
        {
            inSong.UnionWith(OtherVersionWords.Matches(artist).Select(match => match.Value.ToLowerInvariant()));
        }

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

    /// <summary>
    /// Whether an upload's real title says it is the audio over a picture: "official audio", "audio", "lyrics", a
    /// lyric video, a visualizer or a static video, as a whole word in a bracket or after a separator
    /// ("Title // Lyrics"). A word the song's own title has ("Audio Girl") does not count.
    /// </summary>
    /// <param name="songTitle">The song's title.</param>
    /// <param name="videoTitle">The video's real title, decorations and all.</param>
    public static bool IsAudioOnly(string songTitle, string videoTitle)
    {
        ArgumentNullException.ThrowIfNull(songTitle);
        ArgumentNullException.ThrowIfNull(videoTitle);

        var inSong = NotAVideoWords.Matches(songTitle).Select(match => match.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        return AudioOnlyWords.Matches(videoTitle)
            .Select(match => match.Groups["word"].Value)
            .Any(word => !AudioWordsOf(word).Any(inSong.Contains));
    }

    /// <summary>The single words of a matched audio-only phrase ("official audio" is "audio"), lower case.</summary>
    private static IEnumerable<string> AudioWordsOf(string phrase) =>
        NotAVideoWords.Matches(phrase).Select(match => match.Value.ToLowerInvariant());

    /// <summary>
    /// Whether a YouTube channel's name is one of the song's artists: lowercase letters and digits only, without a
    /// trailing "vevo", "official", "officialchannel", "music", "tv" or "channel", and either name holding the other
    /// ("ogafroman" holds "afroman"; "HueyLewisTheNewsVEVO" holds "hueylewisthenews").
    /// </summary>
    /// <param name="channelName">The channel's name, as oEmbed gives it.</param>
    /// <param name="songArtists">The song's artists.</param>
    public static bool ChannelNamesArtist(string channelName, IReadOnlyList<string> songArtists)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(songArtists);

        if (ArtistEvidence.NamesAnyOf(channelName, songArtists))
        {
            return true;
        }

        var channel = ChannelKey(channelName);

        if (channel.Length < MinimumNameLength)
        {
            return false;
        }

        foreach (var artist in songArtists)
        {
            var key = LettersAndDigits(artist);

            if (key.Length >= MinimumNameLength
                && (channel.Contains(key, StringComparison.Ordinal) || key.Contains(channel, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ChannelKey(string name)
    {
        var key = LettersAndDigits(name);

        // Repeated: "Queen Official" and "QueenOfficialVEVO" lose every one of the trailing words.
        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var suffix in ChannelSuffixes)
            {
                if (key.Length > suffix.Length && key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    key = key[..^suffix.Length];
                    changed = true;
                }
            }
        }

        return key;
    }

    private static string LettersAndDigits(string name) =>
        new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
