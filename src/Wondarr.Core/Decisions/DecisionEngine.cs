using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.Decisions;

/// <summary>
/// Decides which of a song's search results may be grabbed automatically and in what order, and
/// records every rejection and every score component so the interactive search can explain itself
/// (MATCHING_ENGINE.md §6.2–6.3).
/// </summary>
/// <remarks>
/// <para>
/// Pure and stateless: everything it needs arrives in the <see cref="DecisionContext"/>, the same
/// input always produces the same verdicts, and it evaluates <em>every</em> rule for every candidate
/// rather than stopping at the first failure, so the UI can list all of them.
/// </para>
/// <para>
/// The rules and the expected outcomes are pinned by <c>tests/fixtures/decisions.json</c>.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The engine is registered as a singleton and called through the injected instance; the instance shape is the API the rest of the app is written against.")]
public sealed class DecisionEngine
{
    /// <summary>The score at or above which a free, fast candidate is grabbed without waiting for more results.</summary>
    public const int GoodEnoughScore = 850;

    /// <summary>The cap applied to a candidate whose duration — or the song's — is unknown.</summary>
    public const int UnknownDurationCap = 849;

    /// <summary>
    /// How far below the held file's identity sub-score an automatic upgrade may fall before the
    /// identity rule rejects it (DECISIONS build session 7 #11).
    /// </summary>
    public const int IdentityTolerance = 40;

    /// <summary>
    /// The most a held file's identity sub-score counts for in the identity rule: a hard identity hit
    /// scores 400, which no candidate scored from its name and length can reach.
    /// </summary>
    public const int HeldIdentityCeiling = 360;

    /// <summary>
    /// The title floor: a candidate whose own track title is less similar than this to the song's
    /// (token-sort ratio, 0 to 1) is a different song. Unrelated titles sharing a word score 0.07 to
    /// 0.45 and every title of the golden set that names the song scores 1.0; 0.6 sits well clear of
    /// both, so a one-word subtitle, a missing "The" or a typo still passes.
    /// </summary>
    public const double TitleFloor = 0.6;

    /// <summary>How close a release's album must be to the song's album for a release with an unknown file list.</summary>
    private const double AlbumThreshold = 0.85;

    /// <summary>
    /// The shortest album, in letters and digits, that a release with an unknown file list may be judged to name
    /// as a run of words inside a longer part of its name, unless the album has two words or more. An album called
    /// "Love" or "1" would be in half the names there are.
    /// </summary>
    private const int MinimumAlbumRunLength = 6;

    /// <summary>Below this a file cannot be the song, whatever the source claims.</summary>
    private const long MinimumSizeBytes = 500_000;

    /// <summary>The bitrate a lossless file must at least reach, in kbps (a "FLAC" below this is fake).</summary>
    private const int LosslessBitrateFloor = 300;

    /// <summary>Bytes per second at which one kbps of audio produces one millisecond of sound; 125 bytes/s per kbps.</summary>
    private const int BytesPerSecondPerKbps = 125;

    /// <summary>The folder tokens that say "compilation", where the artist is legitimately absent from the path.</summary>
    private static readonly string[] CompilationMarkerTokens =
        ["various", "va", "compilation", "compilations", "soundtrack", "ost"];

    /// <summary><see cref="CompilationMarkerTokens"/> as a lookup.</summary>
    private static readonly HashSet<string> CompilationMarkers = new(CompilationMarkerTokens, StringComparer.Ordinal);

    /// <summary>The two separators a remote path can use, whichever platform the source runs on.</summary>
    private static readonly char[] PathSeparators = ['\\', '/'];

    /// <summary>The extensions that are audio (MATCHING_ENGINE §6.2).</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "m4a", "aac", "ogg", "oga", "opus", "wav", "aif", "aiff", "ape", "wv", "wma", "alac",
    };

    /// <summary>
    /// Scores and judges every candidate, returning the accepted ones first — best first — and then
    /// the rejected ones by score.
    /// </summary>
    /// <param name="context">The song, its profile and its blocklist.</param>
    /// <param name="candidates">The search results, from any source.</param>
    public IReadOnlyList<CandidateDecision> Evaluate(DecisionContext context, IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);

        var songBaseTitle = VersionFlagParser.Parse(context.SongTitle).BaseTitle;
        var songVariants = CandidateTitleMatcher.SongVariants(context.SongTitle);
        var decisions = new List<CandidateDecision>();

        foreach (var candidate in candidates)
        {
            decisions.Add(Judge(context, candidate, songBaseTitle, songVariants));
        }

        decisions.Sort((left, right) => Compare(context, left, right));

        return decisions;
    }

    /// <summary>
    /// Gets a value indicating whether a candidate is worth grabbing immediately: accepted, at or
    /// above <see cref="GoodEnoughScore"/>, with a known duration, a free upload slot and a fast peer.
    /// </summary>
    /// <param name="decision">The verdict to test.</param>
    public bool IsGoodEnough(CandidateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var availability = decision.Candidate.Availability;

        return decision.Accepted &&
            decision.Score.Total >= GoodEnoughScore &&
            !decision.Score.CappedForUnknownDuration &&
            availability.FreeUploadSlot == true &&
            availability.UploadSpeedBytesPerSecond >= 1_000_000;
    }

    /// <summary>Scores one candidate and collects every rule it fails.</summary>
    private static CandidateDecision Judge(
        DecisionContext context,
        Candidate candidate,
        string songBaseTitle,
        IReadOnlyList<string> songVariants)
    {
        var score = Score(context, candidate, songBaseTitle);
        var similarity = CandidateTitleMatcher.Similarity(songVariants, candidate);

        return new CandidateDecision(candidate, score, Rejections(context, candidate, score, similarity));
    }

    /// <summary>Accepted before rejected, then by score, then by the tie-breaks of MATCHING_ENGINE §6.3.</summary>
    private static int Compare(DecisionContext context, CandidateDecision left, CandidateDecision right)
    {
        if (left.Accepted != right.Accepted)
        {
            return left.Accepted ? -1 : 1;
        }

        if (left.Score.Total != right.Score.Total)
        {
            return right.Score.Total.CompareTo(left.Score.Total);
        }

        var bySize = CompareSize(context, left.Candidate, right.Candidate);

        return bySize != 0
            ? bySize
            : Fnv1a64(left.Candidate.BlocklistKey).CompareTo(Fnv1a64(right.Candidate.BlocklistKey));
    }

    /// <summary>Smaller lossy files first, larger lossless files first, then the stable hash.</summary>
    private static int CompareSize(DecisionContext context, Candidate left, Candidate right)
    {
        var leftLossless = IsLossless(context, left.QualityId);
        var rightLossless = IsLossless(context, right.QualityId);

        if (leftLossless != rightLossless)
        {
            return leftLossless ? -1 : 1;
        }

        if (left.SizeBytes is not { } leftSize || right.SizeBytes is not { } rightSize)
        {
            if (left.SizeBytes is null && right.SizeBytes is null)
            {
                return 0;
            }

            // An unknown size sorts last either way: it is not evidence in either direction.
            return left.SizeBytes is null ? 1 : -1;
        }

        return leftLossless ? rightSize.CompareTo(leftSize) : leftSize.CompareTo(rightSize);
    }

    /// <summary>Every rule the candidate fails, in the order the reasons are declared.</summary>
    private static List<Rejection> Rejections(
        DecisionContext context,
        Candidate candidate,
        ScoreBreakdown score,
        double? similarity)
    {
        var rejections = new List<Rejection>();
        var quality = QualityOf(context, candidate.QualityId);

        if (context.IsBlocklisted(candidate.BlocklistKey))
        {
            rejections.Add(new Rejection(
                RejectionReason.Blocklisted,
                "Blocklisted: this file was rejected before."));
        }

        // A container whose file list is unknown has no extension yet: the release itself is the candidate
        // and the song's file is found once the client has the metadata or the post is unpacked (DECISIONS
        // build session 8 #3). Every other candidate without an audio extension is not audio.
        var isAudio = candidate.Extension is null
            ? IsUnlistedContainer(candidate)
            : AudioExtensions.Contains(candidate.Extension);

        if (!isAudio)
        {
            rejections.Add(new Rejection(
                RejectionReason.NotAudio,
                string.Concat("Not audio: ", DescribeExtension(candidate.Extension), " is not an audio file.")));
        }

        if (!context.Profile.IsAllowed(candidate.QualityId))
        {
            rejections.Add(new Rejection(
                RejectionReason.FormatNotAllowed,
                string.Concat(
                    "Format not allowed: ",
                    QualityName(quality, candidate.QualityId),
                    " is outside the ",
                    context.Profile.Name,
                    " profile.")));
        }

        if (context.CurrentFileQualityId is { } current &&
            !context.IsManualGrab &&
            !context.Profile.IsUpgrade(current, candidate.QualityId, stopAtCutoff: true))
        {
            // An automatic grab stops at the cutoff (owner question 20): a song whose file already
            // meets it is done, whatever better release turns up.
            rejections.Add(new Rejection(
                RejectionReason.NotAnUpgrade,
                context.Profile.MeetsCutoff(current) && context.Profile.UpgradeAllowed
                    ? string.Concat(
                        "The current file (",
                        QualityName(QualityOf(context, current), current),
                        ") already meets the ",
                        context.Profile.Name,
                        " profile's cutoff.")
                    : string.Concat(
                        "Not an upgrade over the current file (",
                        QualityName(QualityOf(context, current), current),
                        ").")));
        }

        // The identity rule for upgrades (MATCHING_ENGINE §6.6): a strictly better file may replace
        // the held one, but never one that matches the song clearly less well. Only applies when the
        // held file's own identity score is known, and never to a grab the user asked for by hand.
        // The tolerance and the ceiling keep a near-perfect (or hard-hit) held score from blocking
        // every candidate; fingerprint confirmation of automatic upgrades does the fine work.
        if (context.CurrentFileIdentityScore is { } held &&
            !context.IsManualGrab &&
            score.Identity < Math.Min(held, HeldIdentityCeiling) - IdentityTolerance)
        {
            rejections.Add(new Rejection(
                RejectionReason.WorseIdentity,
                string.Concat(
                    "Identity score ",
                    score.Identity.ToString(CultureInfo.InvariantCulture),
                    " is more than ",
                    IdentityTolerance.ToString(CultureInfo.InvariantCulture),
                    " below the current file's ",
                    held.ToString(CultureInfo.InvariantCulture),
                    ".")));
        }

        AddDurationRejection(context, candidate, rejections);
        AddVersionRejection(context, candidate, rejections);
        AddTitleRejection(context, candidate, similarity, rejections);

        if (IsUnlistedContainer(candidate))
        {
            AddUnlistedIdentityRejection(context, candidate, rejections);
        }
        else
        {
            AddArtistRejection(context, candidate, similarity, rejections);
        }

        AddSizeRejection(context, candidate, quality, rejections);
        AddUntaggableRejection(context, candidate, rejections);

        if (candidate.SourceType == SourceTypes.Torznab && candidate.Availability.Seeders is 0)
        {
            rejections.Add(new Rejection(
                RejectionReason.NoSeeders,
                "No seeders: the indexer lists nobody sharing this torrent."));
        }

        // Only a post that downloads whole is held to the limit. A clean post is trimmed to the wanted
        // files by SABnzbd (DECISIONS build session 8 #10), and its matched file is already bounded by
        // the matcher's size window.
        if (candidate.SourceType == SourceTypes.Newznab
            && candidate.Release is { FileIndex: null, Size: { } postSize }
            && postSize > context.MaxContainerSizeBytes)
        {
            rejections.Add(new Rejection(
                RejectionReason.ContainerTooLarge,
                string.Concat(
                    "Too large: the post is ",
                    (postSize / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
                    " MB and downloads whole; the limit is ",
                    (context.MaxContainerSizeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
                    " MB.")));
        }

        if (candidate.IsLocked)
        {
            rejections.Add(new Rejection(
                RejectionReason.Locked,
                "Locked: the source reports this file as locked."));
        }

        if (candidate.Provider is { } provider && context.IgnoredUsers.Contains(provider))
        {
            rejections.Add(new Rejection(
                RejectionReason.IgnoredUser,
                string.Concat("Ignored user: ", provider, " is on the ignore list.")));
        }

        if (candidate.Provider is { } failuresProvider &&
            context.Reputation.TryGetValue(failuresProvider, out var reputation) &&
            reputation.FailuresLast24Hours >= 2)
        {
            rejections.Add(new Rejection(
                RejectionReason.UserOnCooldown,
                string.Concat(
                    "User ",
                    failuresProvider,
                    " failed ",
                    reputation.FailuresLast24Hours.ToString(CultureInfo.InvariantCulture),
                    " times in the last 24 hours.")));
        }

        // Checked last: it depends on the score, and a candidate is only "below the minimum" once
        // every other rule has had its say.
        if (context.Profile.MinScore > 0 && score.Total < context.Profile.MinScore)
        {
            rejections.Add(new Rejection(
                RejectionReason.BelowMinimumScore,
                string.Concat(
                    "Score ",
                    score.Total.ToString(CultureInfo.InvariantCulture),
                    " is below the profile minimum ",
                    context.Profile.MinScore.ToString(CultureInfo.InvariantCulture),
                    ".")));
        }

        return rejections;
    }

    private static void AddDurationRejection(DecisionContext context, Candidate candidate, List<Rejection> rejections)
    {
        if (context.SongDurationMs is not { } songMs || candidate.DurationMs is not { } candidateMs)
        {
            return;
        }

        var differenceMs = Math.Abs(songMs - candidateMs);
        var toleranceMs = Tolerance(context);

        if (differenceMs > toleranceMs)
        {
            rejections.Add(new Rejection(
                RejectionReason.DurationOutOfTolerance,
                string.Concat(
                    "Duration ",
                    Seconds(candidateMs),
                    " s is ",
                    Seconds(differenceMs),
                    " s off (tolerance ",
                    Seconds(toleranceMs),
                    " s).")));
        }
    }

    private static void AddVersionRejection(DecisionContext context, Candidate candidate, List<Rejection> rejections)
    {
        var difference = (candidate.Parsed.VersionFlags ^ context.SongFlags) & VersionFlagNames.HardFlags;

        if (difference == VersionFlags.None)
        {
            return;
        }

        var candidateOnly = JoinFlags(candidate.Parsed.VersionFlags & difference);
        var songOnly = JoinFlags(context.SongFlags & difference);

        rejections.Add(new Rejection(
            RejectionReason.VersionMismatch,
            string.Concat(
                "Version mismatch: candidate is ",
                candidateOnly.Length > 0 ? candidateOnly : "not",
                ", the song is ",
                songOnly.Length > 0 ? songOnly : "not",
                ".")));
    }

    private static void AddTitleRejection(
        DecisionContext context,
        Candidate candidate,
        double? similarity,
        List<Rejection> rejections)
    {
        if (similarity is not { } value || value >= TitleFloor || IsTransliteration(context, candidate))
        {
            return;
        }

        rejections.Add(new Rejection(
            RejectionReason.TitleMismatch,
            string.Concat(
                "Title mismatch: '",
                FileTitle(candidate),
                "' does not match '",
                context.SongTitle,
                "'.")));
    }

    /// <summary>
    /// A song titled in one script and a file named in another cannot be compared letter by letter: the
    /// title floor stands down when the artist is genuinely matched (not by the compilation skip) and
    /// the length is within tolerance, which is what the fingerprint check after the download confirms.
    /// </summary>
    private static bool IsTransliteration(DecisionContext context, Candidate candidate)
    {
        var fileTitle = FileTitle(candidate);

        if (CandidateTitleMatcher.IsLatinScript(context.SongTitle) is not { } songLatin ||
            CandidateTitleMatcher.IsLatinScript(fileTitle) is not { } fileLatin ||
            songLatin == fileLatin)
        {
            return false;
        }

        return context.MainArtists.Count > 0 &&
            ArtistGenuinelyNamed(context, candidate) &&
            context.SongDurationMs is { } songMs &&
            candidate.DurationMs is { } candidateMs &&
            Math.Abs(songMs - candidateMs) <= Tolerance(context);
    }

    private static void AddArtistRejection(
        DecisionContext context,
        Candidate candidate,
        double? similarity,
        List<Rejection> rejections)
    {
        if (context.MainArtists.Count == 0)
        {
            return;
        }

        // A compilation path ("Various Artists", "OST") never stands in for the artist, and a token or
        // two of the artist's name in a folder (the "Days" of "Nine Days") is not the artist either: the
        // artist must be the file name's or a whole folder's (an artist folder above an OST album), or
        // the file's own title must be the song's title in full. Checked before the ordinary overlap
        // for exactly that reason.
        if (ContainsCompilationMarker(candidate.RemotePath))
        {
            if (CompilationPathNamesArtist(context, candidate) ||
                similarity is null ||
                CandidateTitleMatcher.IsFullTitleMatch(context.SongTitle, candidate, context.SongFlags))
            {
                return;
            }

            rejections.Add(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: Various Artists path without the artist or the full title."));

            return;
        }

        if (ArtistOverlap(context, candidate) > 0)
        {
            return;
        }

        rejections.Add(new Rejection(
            RejectionReason.ArtistMismatch,
            "Artist mismatch: none of the song's artists appears in the candidate's path."));
    }

    /// <summary>
    /// Whether the artist is genuinely matched: on a compilation path only the file name or a whole
    /// folder can name it; elsewhere any overlap with the path counts.
    /// </summary>
    private static bool ArtistGenuinelyNamed(DecisionContext context, Candidate candidate) =>
        ContainsCompilationMarker(candidate.RemotePath)
            ? CompilationPathNamesArtist(context, candidate)
            : ArtistOverlap(context, candidate) > 0;

    /// <summary>
    /// On a compilation path: every word of one of the song's artists is in the file name, or a whole
    /// folder name is one of the song's artists (<c>Hans Zimmer/Inception OST/05 Time.flac</c>). A word
    /// or two inside a longer folder name never counts.
    /// </summary>
    private static bool CompilationPathNamesArtist(DecisionContext context, Candidate candidate)
    {
        if (FileNameNamesArtist(context, candidate))
        {
            return true;
        }

        var segments = candidate.RemotePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var folder = TextMatching.NormalizeArtist(segments[index]);

            if (folder.Length > 0 && context.MainArtists.Any(artist => TextMatching.NormalizeArtist(artist) == folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether every word of one of the song's artists is in the file name (not the folders).</summary>
    private static bool FileNameNamesArtist(DecisionContext context, Candidate candidate)
    {
        var fileTokens = TokenizePath(LastSegment(candidate.RemotePath));

        foreach (var artist in context.MainArtists)
        {
            var tokens = TextMatching.NormalizeArtist(artist).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length > 0 && tokens.All(fileTokens.Contains))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The candidate's own track title for messages: what the parser found, else the file name's.</summary>
    private static string FileTitle(Candidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Parsed.Title)
            ? candidate.Parsed.Title
            : CandidateTitleMatcher.ExtractTitle(candidate.RemotePath) ?? candidate.DisplayName;

    /// <summary>
    /// A container the tag writer cannot write to is turned away here, unless the library's output
    /// policy converts that kind of source to one it can (a lossless rule that turns an APE into a
    /// FLAC). The import converts before it tags, so a converted file never reaches the writer in its
    /// original container. A tagging failure is never blocklisted, so without this rule the same best
    /// candidate would be grabbed, downloaded and failed again on every backoff (R-049).
    /// </summary>
    private static void AddUntaggableRejection(
        DecisionContext context,
        Candidate candidate,
        List<Rejection> rejections)
    {
        // Not audio at all is the NotAudio rule's to say; this rule is about audio Wondarr cannot tag.
        if (candidate.Extension is null
            || !AudioExtensions.Contains(candidate.Extension)
            || TagWriter.CanTag(candidate.Extension))
        {
            return;
        }

        var rule = context.OutputPolicy?.RuleFor(candidate.SourceType, IsLossless(context, candidate.QualityId));

        if (rule is not null && rule.Codec != OutputCodec.Keep)
        {
            return;
        }

        rejections.Add(new Rejection(
            RejectionReason.UntaggableFormat,
            string.Concat(
                "Cannot tag ",
                DescribeExtension(candidate.Extension),
                " files: Wondarr writes tags to MP3, FLAC, Opus, Ogg, M4A, AAC, WAV and AIFF only, ",
                "and the library has no conversion rule that turns this into one of them.")));
    }

    private static void AddSizeRejection(
        DecisionContext context,
        Candidate candidate,
        Quality? quality,
        List<Rejection> rejections)
    {
        if (!FailsSizeSanity(context, candidate, quality))
        {
            return;
        }

        var size = candidate.SizeBytes ?? 0;
        var durationMs = candidate.DurationMs ?? context.SongDurationMs ?? 0;

        rejections.Add(new Rejection(
            RejectionReason.SizeSanity,
            string.Concat(
                "Size sanity: ",
                size.ToString(CultureInfo.InvariantCulture),
                " bytes is impossible for ",
                Seconds(durationMs),
                " s at ",
                QualityName(quality, candidate.QualityId),
                ".")));
    }

    /// <summary>Too small to be anything, or outside the window its duration and quality allow.</summary>
    private static bool FailsSizeSanity(DecisionContext context, Candidate candidate, Quality? quality)
    {
        if (candidate.SizeBytes is not { } size)
        {
            return false;
        }

        if (size < MinimumSizeBytes)
        {
            return true;
        }

        var durationMs = candidate.DurationMs ?? context.SongDurationMs;

        if (durationMs is not { } ms || ms <= 0 || quality is null)
        {
            return false;
        }

        var seconds = ms / 1000.0;

        if (quality.Lossless)
        {
            return size < seconds * LosslessBitrateFloor * BytesPerSecondPerKbps;
        }

        if (quality.MinBitrate is { } minimum && quality.MaxBitrate is { } maximum)
        {
            var expected = seconds * BytesPerSecondPerKbps;

            return size < expected * minimum * 0.7 || size > expected * maximum * 1.5;
        }

        return false;
    }

    /// <summary>The full score breakdown of one candidate.</summary>
    private static ScoreBreakdown Score(DecisionContext context, Candidate candidate, string songBaseTitle)
    {
        var title = Round(TextMatching.Similarity(songBaseTitle, CandidateTitle(candidate)) * 200);
        var artist = Round(ArtistOverlap(context, candidate) * 100);
        var duration = DurationScore(context, candidate);
        var pathPenalty = PathVersionPenalty(context, candidate);
        var identity = Math.Max(0, title + artist + duration - pathPenalty);
        var quality = QualityScore(context, candidate.QualityId);
        var availability = AvailabilityScore(candidate);
        var sourcePreference = SourcePreferenceScore(candidate.SourceType);

        var adjustments = Adjustments(context, candidate, pathPenalty);
        var adjustmentTotal = Math.Clamp(adjustments.Sum(adjustment => adjustment.Points), -50, 50);

        var total = Math.Clamp(
            identity + quality + availability + sourcePreference + adjustmentTotal,
            0,
            1000);

        var cappedForUnknownDuration = context.SongDurationMs is null || candidate.DurationMs is null;

        if (cappedForUnknownDuration)
        {
            total = Math.Min(total, UnknownDurationCap);
        }

        return new ScoreBreakdown(
            title,
            artist,
            duration,
            identity,
            quality,
            availability,
            sourcePreference,
            adjustments,
            adjustmentTotal,
            total,
            cappedForUnknownDuration);
    }

    /// <summary>100 at no difference, decaying with spotDL's shape to 0 at the tolerance; 40 when unknown.</summary>
    private static int DurationScore(DecisionContext context, Candidate candidate)
    {
        if (context.SongDurationMs is not { } songMs || candidate.DurationMs is not { } candidateMs)
        {
            return 40;
        }

        var differenceMs = Math.Abs(songMs - candidateMs);

        if (differenceMs > Tolerance(context))
        {
            return 0;
        }

        return Round(100 * Math.Exp(-0.1 * (differenceMs / 1000.0)));
    }

    /// <summary>The share of the song's artist tokens that occur in the candidate's path, 1.0 at best.</summary>
    private static double ArtistOverlap(DecisionContext context, Candidate candidate)
    {
        if (context.MainArtists.Count == 0)
        {
            return 0;
        }

        // A source whose candidates carry parsed metadata (YouTube: the video id is the path, the
        // artist is metadata) is matched on that; a path-shaped candidate (Soulseek) is matched on
        // its path tokens, where the artist legitimately appears.
        // The file name is looked at as well: "Hey Jude - The Beatles.mp3" parses the artist as "Hey Jude".
        var candidateTokens = !string.IsNullOrWhiteSpace(candidate.Parsed.Artist)
            ? new HashSet<string>(
                TokenizePath(candidate.Parsed.Artist).Concat(TokenizePath(LastSegment(candidate.RemotePath))),
                StringComparer.Ordinal)
            : TokenizePath(candidate.RemotePath);
        var best = 0.0;

        foreach (var artist in context.MainArtists)
        {
            var tokens = TextMatching.NormalizeArtist(artist).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                continue;
            }

            var found = 0;

            foreach (var token in tokens)
            {
                if (candidateTokens.Contains(token))
                {
                    found++;
                }
            }

            best = Math.Max(best, (double)found / tokens.Length);
        }

        return best;
    }

    /// <summary>300 at or above the cutoff, less 60 per allowed group in between, 0 when the quality is unknown.</summary>
    private static int QualityScore(DecisionContext context, long qualityId)
    {
        var candidateIndex = context.Profile.GroupIndexOf(qualityId);

        if (candidateIndex is not { } index || context.Profile.GroupIndexOf(context.Profile.CutoffQualityId) is not { } cutoffIndex)
        {
            return 0;
        }

        if (index >= cutoffIndex)
        {
            return 300;
        }

        var allowedGroupsBelow = 0;

        for (var position = index; position < cutoffIndex; position++)
        {
            if (context.Profile.Items[position].Allowed)
            {
                allowedGroupsBelow++;
            }
        }

        return Math.Max(0, 300 - (60 * allowedGroupsBelow));
    }

    /// <summary>
    /// Soulseek: free slot +80, queue up to +40, upload speed up to +30. Torrents: seeders log-scaled to
    /// +100, freeleech +20, a file list read before the grab +30. Usenet: age under 1 000 days up to
    /// +100, grabs +20 (MATCHING_ENGINE §3).
    /// </summary>
    private static int AvailabilityScore(Candidate candidate)
    {
        var availability = candidate.Availability;

        if (candidate.SourceType == SourceTypes.Torznab)
        {
            var seeders = Math.Max(0, availability.Seeders ?? 0);

            // 1 seeder ≈ 30, 10 ≈ 100 (log10 scaled, capped).
            var seederScore = seeders == 0 ? 0 : (int)Math.Min(100, Math.Round(30 + (70 * Math.Log10(seeders))));

            return seederScore + (availability.Freeleech ? 20 : 0) + (availability.FileListKnown ? 30 : 0);
        }

        if (candidate.SourceType == SourceTypes.Newznab)
        {
            var age = availability.AgeDays is { } days ? Math.Max(0, 100 - (days / 10)) : 50;

            return age + (availability.Grabs is > 0 ? 20 : 0) + (availability.FileListKnown ? 30 : 0);
        }

        var score = availability.FreeUploadSlot == true ? 80 : 0;

        if (availability.QueueLength is { } queue)
        {
            score += 40 * Math.Max(0, 20 - queue) / 20;
        }

        if (availability.UploadSpeedBytesPerSecond is { } speed)
        {
            if (speed >= 1_000_000)
            {
                score += 30;
            }
            else if (speed >= 350_000)
            {
                score += 20;
            }
            else if (speed >= 100_000)
            {
                score += 10;
            }
        }

        return score;
    }

    /// <summary>
    /// The tier a source runs in (MATCHING_ENGINE §6.3–6.4): Soulseek 1, YouTube 2, torrents and usenet 3.
    /// Wondarr has no configurable source order or per-instance offset, so the default order is the order.
    /// A source type this does not know scores as tier 3.
    /// </summary>
    /// <param name="sourceType">One of <see cref="SourceTypes"/>.</param>
    /// <returns>1, 2 or 3.</returns>
    public static int SourceTierOf(string sourceType) => sourceType switch
    {
        SourceTypes.Soulseek => 1,
        SourceTypes.YouTube => 2,
        _ => 3,
    };

    /// <summary>Tier 1 = 100, tier 2 = 60, tier 3 = 30.</summary>
    private static int SourcePreferenceScore(string sourceType) => SourceTierOf(sourceType) switch
    {
        1 => 100,
        2 => 60,
        _ => 30,
    };

    /// <summary>Every named adjustment applied to this candidate, including the informational path penalty.</summary>
    private static List<ScoreAdjustment> Adjustments(DecisionContext context, Candidate candidate, int pathPenalty)
    {
        var adjustments = new List<ScoreAdjustment>();

        switch (candidate.VideoKind)
        {
            case YouTubeVideoKinds.ArtTrack:
                adjustments.Add(new ScoreAdjustment("artTrack", 40));
                break;
            case YouTubeVideoKinds.OfficialVideo:
                adjustments.Add(new ScoreAdjustment("officialVideo", -20));
                break;
            case YouTubeVideoKinds.UserUpload:
                adjustments.Add(new ScoreAdjustment("userUpload", -40));
                break;
        }

        if (!candidate.Parsed.HasUnexplainedBrackets)
        {
            adjustments.Add(new ScoreAdjustment("bracketCheck", 15));
        }

        if (!string.IsNullOrWhiteSpace(context.AlbumTitle) &&
            !string.IsNullOrWhiteSpace(candidate.Parsed.Album) &&
            TextMatching.Similarity(context.AlbumTitle, candidate.Parsed.Album!) >= 0.9)
        {
            adjustments.Add(new ScoreAdjustment("albumMatch", 20));
        }

        if (context.TrackNo is { } songTrack && candidate.Parsed.TrackNo == songTrack)
        {
            adjustments.Add(new ScoreAdjustment("trackMatch", 10));
        }

        if (candidate.Provider is { } provider && context.Reputation.TryGetValue(provider, out var reputation))
        {
            var points = Math.Min(40, 20 * reputation.Successes) - (40 * reputation.Failures);

            if (points != 0)
            {
                adjustments.Add(new ScoreAdjustment("reputation", points));
            }
        }

        var songExplicit = (context.SongFlags & VersionFlags.Explicit) != VersionFlags.None;
        var candidateExplicit = (candidate.Parsed.VersionFlags & VersionFlags.Explicit) != VersionFlags.None;

        if (songExplicit != candidateExplicit)
        {
            adjustments.Add(new ScoreAdjustment("explicitMismatch", -5));
        }

        // Informational only: the penalty is already inside Identity, and counting it again would
        // charge the candidate twice for the same folder.
        if (pathPenalty > 0)
        {
            adjustments.Add(new ScoreAdjustment("pathVersion", -pathPenalty));
        }

        return adjustments;
    }

    /// <summary>150 when the path implies a hard flag the song does not have; otherwise 0.</summary>
    private static int PathVersionPenalty(DecisionContext context, Candidate candidate)
    {
        var unhintedBySong = candidate.Parsed.PathVersionFlags & VersionFlagNames.HardFlags & ~context.SongFlags;

        return unhintedBySong == VersionFlags.None ? 0 : 150;
    }

    /// <summary>The tolerance for this song, in milliseconds.</summary>
    private static int Tolerance(DecisionContext context) =>
        context.DurationToleranceMs ?? context.Profile.DurationToleranceMs;

    /// <summary>The candidate's base title, falling back to the file name when the parse found none.</summary>
    private static string CandidateTitle(Candidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Parsed.Title)
            ? candidate.Parsed.Title
            : Path.GetFileNameWithoutExtension(LastSegment(candidate.RemotePath));

    /// <summary>The last segment of a remote path, whichever separator the source uses.</summary>
    private static string LastSegment(string remotePath)
    {
        var segments = remotePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0 ? remotePath : segments[^1];
    }

    /// <summary>The normalised tokens of a remote path, so an artist can be looked for anywhere in it.</summary>
    private static HashSet<string> TokenizePath(string remotePath) =>
        new(TextMatching.Normalize(remotePath.Replace('\\', ' ').Replace('/', ' '))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    /// <summary>Whether the path carries a folder token that marks a compilation.</summary>
    private static bool ContainsCompilationMarker(string remotePath)
    {
        foreach (var token in TokenizePath(remotePath))
        {
            if (CompilationMarkers.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A torrent or NZB whose file list was not read before the grab (a magnet, a RAR set, an obfuscated
    /// post): the release is the candidate and it names no file.
    /// </summary>
    private static bool IsUnlistedContainer(Candidate candidate) =>
        candidate.SourceType is SourceTypes.Torznab or SourceTypes.Newznab
        && candidate.Container == CandidateContainer.AlbumContainer
        && candidate.Release is { FileIndex: null };

    /// <summary>
    /// Identity for a release whose file list is unknown. No file name has been seen, so the release name is
    /// all there is, and it must carry the song: it starts with the song's artist, whole (not a surname in
    /// common, not a word of it), and it names the song's album or the song itself. A compilation, a
    /// soundtrack or a various-artists set is turned away outright, since it could hold anyone's song. Such a
    /// release is a whole-album download; grabbing the wrong one costs the album, so the bar is high.
    /// </summary>
    private static void AddUnlistedIdentityRejection(DecisionContext context, Candidate candidate, List<Rejection> rejections)
    {
        var name = candidate.Release?.Title ?? candidate.RemotePath;

        if (ContainsCompilationMarker(name))
        {
            rejections.Add(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: a compilation or soundtrack release whose file list is unknown could hold anyone's song."));

            return;
        }

        if (context.MainArtists.Count > 0
            && !context.MainArtists.Any(artist => ReleaseNamesArtist(artist, name, candidate.Parsed.Artist)))
        {
            rejections.Add(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: the release name does not start with the song's artist, in full."));

            return;
        }

        if (!ReleaseNamesAlbumOrTitle(context, SongAlbums(context, candidate), name, ArtistPrefixEnd(context.MainArtists, name), candidate.Parsed.Album))
        {
            rejections.Add(new Rejection(
                RejectionReason.TitleMismatch,
                "Title mismatch: the release names neither the song's album nor the song, and its file list is unknown."));
        }
    }

    /// <summary>
    /// The albums a release of unknown contents may be named after for this song. A container candidate carries
    /// what its provider decided: the album of the MusicBrainz release the search targeted, and the song's album
    /// context unless that is a pseudo-album. A song filed under "Singles" would otherwise have no album that any
    /// release is named after. A provider that decided nothing (a candidate stored before the request carried
    /// albums, a push that matched the release by the song's title) leaves the context's title as the album,
    /// unless that is a pseudo-album's, which no release is named after.
    /// </summary>
    private static List<string> SongAlbums(DecisionContext context, Candidate candidate)
    {
        var request = candidate.Release?.Song;
        string?[] albums = [request?.SongAlbum, request?.TargetAlbum];

        if (albums.All(string.IsNullOrWhiteSpace))
        {
            albums = [SongAlbumNames.Of(context.AlbumKind, context.AlbumTitle)];
        }

        return [.. albums.Where(album => !string.IsNullOrWhiteSpace(album)).Select(album => album!).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether a release name starts with the artist as a whole: its parsed artist is the artist, or the text
    /// before some hyphen is (<c>A-ha-Hunting_High_and_Low</c> parses the artist as "A", but "A-ha" is the text
    /// before the second hyphen).
    /// </summary>
    private static bool ReleaseNamesArtist(string artist, string releaseName, string? parsedArtist)
    {
        var wanted = SceneFileName.ArtistKey(artist);

        // An artist with no letters or digits (a name made of punctuation) cannot be compared.
        if (wanted.Length == 0 || (parsedArtist is not null && SceneFileName.ArtistKey(parsedArtist) == wanted))
        {
            return true;
        }

        return ArtistPrefixEnd([artist], releaseName) >= 0;
    }

    /// <summary>
    /// The index of the hyphen that ends the artist a release name starts with: the last one with the text
    /// before it spelling one of the artists (<see cref="ReleaseNamesArtist"/>), or -1 when none does.
    /// </summary>
    private static int ArtistPrefixEnd(IReadOnlyList<string> artists, string releaseName)
    {
        var wanted = artists
            .Select(SceneFileName.ArtistKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var end = -1;

        for (var index = releaseName.IndexOf('-', StringComparison.Ordinal); index > 0; index = releaseName.IndexOf('-', index + 1))
        {
            if (wanted.Contains(SceneFileName.ArtistKey(releaseName[..index])))
            {
                end = index;
            }
        }

        return end;
    }

    /// <summary>
    /// Whether a release name is one of the song's albums (a hyphen-separated field of it after the artist, or a
    /// run of up to four, or its parsed album, as close as <see cref="AlbumThreshold"/>; or the album as a run of
    /// whole words anywhere after the artist, for the names whose album shares a field with its tags) or holds
    /// the song's title as a run of whole words.
    /// </summary>
    /// <param name="context">The wanted song.</param>
    /// <param name="albums">The albums the release may be named after.</param>
    /// <param name="releaseName">The release name.</param>
    /// <param name="artistEnd">
    /// The index of the hyphen that ends the artist the name starts with, or -1 when that is not known. The
    /// artist is left out of the album's search: an album named like its artist (Boston's "Boston") must be
    /// found after the artist, not in it.
    /// </param>
    /// <param name="parsedAlbum">The album the release name parses to.</param>
    private static bool ReleaseNamesAlbumOrTitle(DecisionContext context, List<string> albums, string releaseName, int artistEnd, string? parsedAlbum)
    {
        if (albums.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(parsedAlbum)
                && albums.Any(album => TextMatching.Similarity(album, parsedAlbum) >= AlbumThreshold))
            {
                return true;
            }

            var afterArtist = artistEnd >= 0 ? releaseName[(artistEnd + 1)..] : releaseName;
            var fields = afterArtist.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var start = 0; start < fields.Length; start++)
            {
                for (var length = 1; length <= 4 && start + length <= fields.Length; length++)
                {
                    var run = string.Join(' ', fields.Skip(start).Take(length));

                    if (albums.Any(album => TextMatching.Similarity(album, run) >= AlbumThreshold))
                    {
                        return true;
                    }
                }
            }

            // "Norah.Jones.-.Come.Away.With.Me.24bit.192Khz.hdtracks.FLAC" has one hyphen, and what follows it is the
            // album and its tags in one field. Only where the artist was found, so there is something to leave out.
            if (artistEnd >= 0 && albums.Any(album => NamesAlbumAsWords(album, afterArtist)))
            {
                return true;
            }
        }

        var title = TextMatching.Normalize(VersionFlagParser.Parse(context.SongTitle).BaseTitle);

        return title.Length > 0
            && string.Concat(" ", TextMatching.Normalize(releaseName), " ").Contains(string.Concat(" ", title, " "), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the album occurs as a run of whole words in the part of a release name after its artist. An album
    /// of one short word is never looked for this way, since it would match any name that holds the word.
    /// </summary>
    private static bool NamesAlbumAsWords(string album, string afterArtist)
    {
        var wanted = TextMatching.Normalize(album);

        if (!wanted.Contains(' ') && wanted.Length < MinimumAlbumRunLength)
        {
            return false;
        }

        return string.Concat(" ", TextMatching.Normalize(afterArtist), " ").Contains(string.Concat(" ", wanted, " "), StringComparison.Ordinal);
    }

    private static bool IsLossless(DecisionContext context, long qualityId) =>
        context.Qualities.TryGetValue(qualityId, out var quality) && quality.Lossless;

    private static Quality? QualityOf(DecisionContext context, long qualityId) =>
        context.Qualities.TryGetValue(qualityId, out var quality) ? quality : null;

    /// <summary>The quality's name, or its id when the seed does not know it.</summary>
    private static string QualityName(Quality? quality, long qualityId) =>
        quality?.Name ?? string.Concat("quality ", qualityId.ToString(CultureInfo.InvariantCulture));

    private static string DescribeExtension(string? extension) =>
        string.IsNullOrWhiteSpace(extension) ? "an unknown extension" : string.Concat('.', extension);

    /// <summary>Whole seconds of a millisecond value, for messages.</summary>
    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The wire names of the flags in a mask, comma-separated.</summary>
    private static string JoinFlags(VersionFlags flags) => string.Join(", ", VersionFlagNames.ToWireNames(flags));

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// FNV-1a over the UTF-8 bytes of the key: the stable tie-break that <see cref="string.GetHashCode()"/>
    /// cannot provide, as it is randomised per process.
    /// </summary>
    private static ulong Fnv1a64(string value)
    {
        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        var hash = Offset;

        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            hash ^= octet;
            hash *= Prime;
        }

        return hash;
    }
}
