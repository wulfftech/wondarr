using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

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
        var decisions = new List<CandidateDecision>();

        foreach (var candidate in candidates)
        {
            decisions.Add(Judge(context, candidate, songBaseTitle));
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
    private static CandidateDecision Judge(DecisionContext context, Candidate candidate, string songBaseTitle)
    {
        var score = Score(context, candidate, songBaseTitle);

        return new CandidateDecision(candidate, score, Rejections(context, candidate, score));
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
    private static List<Rejection> Rejections(DecisionContext context, Candidate candidate, ScoreBreakdown score)
    {
        var rejections = new List<Rejection>();
        var quality = QualityOf(context, candidate.QualityId);

        if (context.IsBlocklisted(candidate.BlocklistKey))
        {
            rejections.Add(new Rejection(
                RejectionReason.Blocklisted,
                "Blocklisted: this file was rejected before."));
        }

        if (candidate.Extension is null || !AudioExtensions.Contains(candidate.Extension))
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
            !context.Profile.IsUpgrade(current, candidate.QualityId))
        {
            rejections.Add(new Rejection(
                RejectionReason.NotAnUpgrade,
                string.Concat(
                    "Not an upgrade over the current file (",
                    QualityName(QualityOf(context, current), current),
                    ").")));
        }

        AddDurationRejection(context, candidate, rejections);
        AddVersionRejection(context, candidate, rejections);
        AddArtistRejection(context, candidate, rejections);
        AddSizeRejection(context, candidate, quality, rejections);

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

    private static void AddArtistRejection(DecisionContext context, Candidate candidate, List<Rejection> rejections)
    {
        if (context.MainArtists.Count == 0 || ArtistOverlap(context, candidate) > 0)
        {
            return;
        }

        // Compilations ("Various Artists", "OST") legitimately carry no artist in the path.
        if (ContainsCompilationMarker(candidate.RemotePath))
        {
            return;
        }

        rejections.Add(new Rejection(
            RejectionReason.ArtistMismatch,
            "Artist mismatch: none of the song's artists appears in the candidate's path."));
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
        var sourcePreference = SourcePreferenceScore(context.SourceTier);

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

        var pathTokens = TokenizePath(candidate.RemotePath);
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
                if (pathTokens.Contains(token))
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

    /// <summary>Free slot +80, queue up to +40, upload speed up to +30.</summary>
    private static int AvailabilityScore(Candidate candidate)
    {
        var availability = candidate.Availability;
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

    /// <summary>Tier 1 = 100, tier 2 = 60, tier 3 = 30.</summary>
    private static int SourcePreferenceScore(int sourceTier) => Math.Clamp(sourceTier, 1, 3) switch
    {
        1 => 100,
        2 => 60,
        _ => 30,
    };

    /// <summary>Every named adjustment applied to this candidate, including the informational path penalty.</summary>
    private static List<ScoreAdjustment> Adjustments(DecisionContext context, Candidate candidate, int pathPenalty)
    {
        var adjustments = new List<ScoreAdjustment>();

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