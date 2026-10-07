using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Decisions;

/// <summary>
/// Why a candidate may not be grabbed automatically (MATCHING_ENGINE.md §6.2). Every rejected
/// candidate carries at least one of these, with a message the interactive search shows.
/// </summary>
public enum RejectionReason
{
    /// <summary>The exact file is on the blocklist.</summary>
    Blocklisted,

    /// <summary>The extension is not an audio file.</summary>
    NotAudio,

    /// <summary>The inferred quality is not allowed by the song's quality profile.</summary>
    FormatNotAllowed,

    /// <summary>The song already has a file of the same or a better quality.</summary>
    NotAnUpgrade,

    /// <summary>The candidate's duration is further from the song's than the tolerance allows.</summary>
    DurationOutOfTolerance,

    /// <summary>The candidate carries a hard version flag the song does not have, or lacks one it has.</summary>
    VersionMismatch,

    /// <summary>No artist token of the song appears in the candidate's path.</summary>
    ArtistMismatch,

    /// <summary>The file size is impossible for its duration and quality.</summary>
    SizeSanity,

    /// <summary>The source reports the file as locked.</summary>
    Locked,

    /// <summary>The provider is on the ignore list.</summary>
    IgnoredUser,

    /// <summary>The provider failed twice in the last 24 hours.</summary>
    UserOnCooldown,

    /// <summary>The candidate's score is below the profile's minimum.</summary>
    BelowMinimumScore,

    /// <summary>The candidate's identity score is below the current file's (MATCHING_ENGINE §6.6).</summary>
    WorseIdentity,
}

/// <summary>Maps <see cref="RejectionReason"/> onto the camel-case wire name the API returns.</summary>
public static class RejectionReasonNames
{
    /// <summary>The camel-case wire name, for example <c>versionMismatch</c>.</summary>
    /// <param name="reason">The rejection reason.</param>
    public static string ToWireName(this RejectionReason reason) =>
        JsonNamingPolicy.CamelCase.ConvertName(reason.ToString());
}

/// <summary>One reason a candidate was rejected, with a message written for people.</summary>
/// <param name="Reason">The rule that fired.</param>
/// <param name="Message">The human-readable explanation, for example "Version mismatch: candidate is live, the song is not".</param>
public sealed record Rejection(RejectionReason Reason, string Message);

/// <summary>One named adjustment to the score, so the UI can explain the total.</summary>
/// <param name="Name">The adjustment's name, for example <c>bracketCheck</c>.</param>
/// <param name="Points">How many points it added (negative when it subtracted).</param>
public sealed record ScoreAdjustment(string Name, int Points);

/// <summary>
/// The 0–1000 score of one candidate, split into the components the interactive search shows
/// (MATCHING_ENGINE.md §6.3).
/// </summary>
/// <param name="Title">0–200: token-sort similarity of the base titles.</param>
/// <param name="Artist">0–100: share of the song's artist tokens found in the candidate's path.</param>
/// <param name="Duration">0–100: 100 at no difference, decaying to 0 at the tolerance; 40 when either duration is unknown.</param>
/// <param name="Identity">0–400: title + artist + duration, less the path-version penalty.</param>
/// <param name="Quality">0–300: 300 at or above the cutoff, less 60 per allowed group below it.</param>
/// <param name="Availability">0–150: slot, queue and upload-speed buckets.</param>
/// <param name="SourcePreference">0–100: from the source tier.</param>
/// <param name="Adjustments">Every named adjustment that was applied, including the informational <c>pathVersion</c> penalty.</param>
/// <param name="AdjustmentTotal">The counted adjustments, clamped to ±50.</param>
/// <param name="Total">The clamped 0–1000 total.</param>
/// <param name="CappedForUnknownDuration">True when the total was capped because a duration is unknown.</param>
public sealed record ScoreBreakdown(
    int Title,
    int Artist,
    int Duration,
    int Identity,
    int Quality,
    int Availability,
    int SourcePreference,
    IReadOnlyList<ScoreAdjustment> Adjustments,
    int AdjustmentTotal,
    int Total,
    bool CappedForUnknownDuration);

/// <summary>One candidate's verdict: the score it would get and every rule it failed.</summary>
/// <param name="Candidate">The candidate this verdict is about.</param>
/// <param name="Score">The score breakdown; computed even for rejected candidates, so the UI can show it.</param>
/// <param name="Rejections">Every rejection that fired — the engine never short-circuits.</param>
public sealed record CandidateDecision(Candidate Candidate, ScoreBreakdown Score, IReadOnlyList<Rejection> Rejections)
{
    /// <summary>Gets a value indicating whether the candidate may be grabbed automatically.</summary>
    public bool Accepted => Rejections.Count == 0;
}

/// <summary>What is known about a provider's track record, used for the reputation adjustment and the cooldown.</summary>
/// <param name="Successes">Verified files this provider has delivered.</param>
/// <param name="Failures">Grabs from this provider that failed or failed verification.</param>
/// <param name="FailuresLast24Hours">Failures in the last 24 hours; two or more put the provider on cooldown.</param>
public sealed record UserReputation(int Successes, int Failures, int FailuresLast24Hours);

/// <summary>
/// Everything the decision engine needs about the wanted song, its profile and its blocklist. Pure
/// data: the engine does no I/O, so the same input always produces the same verdicts.
/// </summary>
public sealed record DecisionContext
{
    /// <summary>The wanted song's title, version hints and all, as MusicBrainz gave it.</summary>
    public string SongTitle { get; init; } = string.Empty;

    /// <summary>The song's main artists.</summary>
    public IReadOnlyList<string> MainArtists { get; init; } = [];

    /// <summary>The song's expected duration in milliseconds, when known.</summary>
    public int? SongDurationMs { get; init; }

    /// <summary>The version flags the song carries.</summary>
    public VersionFlags SongFlags { get; init; } = VersionFlags.None;

    /// <summary>The album the song belongs to, when it has one.</summary>
    public string? AlbumTitle { get; init; }

    /// <summary>The song's track number on <see cref="AlbumTitle"/>, when known.</summary>
    public int? TrackNo { get; init; }

    /// <summary>The profile deciding which qualities are allowed and where the cutoff sits.</summary>
    public required QualityProfile Profile { get; init; }

    /// <summary>Every seeded quality by id, for size sanity and the lossless tie-break.</summary>
    public IReadOnlyDictionary<long, Quality> Qualities { get; init; } = new Dictionary<long, Quality>();

    /// <summary>The quality of the file the song already has, when it has one.</summary>
    public long? CurrentFileQualityId { get; init; }

    /// <summary>
    /// The identity sub-score of the candidate that produced the file the song already holds, when
    /// that is known. An upgrade never trades identity for bitrate: an automatic candidate below it
    /// is rejected (MATCHING_ENGINE §6.6); <see langword="null"/> when the held file's score is
    /// unknown, and then only the quality rule applies.
    /// </summary>
    public int? CurrentFileIdentityScore { get; init; }

    /// <summary>Whether a person asked for this grab by hand; manual grabs are exempt from the upgrade rule.</summary>
    public bool IsManualGrab { get; init; }

    /// <summary>The source's tier, 1 (preferred) to 3.</summary>
    public int SourceTier { get; init; } = 1;

    /// <summary>Overrides the profile's duration tolerance when set.</summary>
    public int? DurationToleranceMs { get; init; }

    /// <summary>Tells whether a blocklist key is blocked. Default: nothing is blocked.</summary>
    public Func<string, bool> IsBlocklisted { get; init; } = AlwaysFalse;

    /// <summary>Providers whose results are ignored; compared ordinal-ignore-case.</summary>
    public IReadOnlySet<string> IgnoredUsers { get; init; } = EmptyUsers;

    /// <summary>What is known about each provider, by provider name; compared ordinal-ignore-case.</summary>
    public IReadOnlyDictionary<string, UserReputation> Reputation { get; init; } = EmptyReputation;

    /// <summary>The shared "nothing is blocked" predicate.</summary>
    private static readonly Func<string, bool> AlwaysFalse = _ => false;

    /// <summary>The shared empty ignore list.</summary>
    private static readonly IReadOnlySet<string> EmptyUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shared empty reputation table.</summary>
    private static readonly IReadOnlyDictionary<string, UserReputation> EmptyReputation =
        new Dictionary<string, UserReputation>(StringComparer.OrdinalIgnoreCase);
}
