using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>
/// The component formulas of the score (MATCHING_ENGINE.md §6.3), the clamp on the adjustments and
/// the determinism of the ordering. The golden file pins the end-to-end verdicts; these pin the parts.
/// </summary>
public class DecisionEngineTests
{
    private const string DefaultRemotePath = "@@b\\Music\\Daft Punk\\Random Access Memories\\08 Get Lucky.mp3";

    private static readonly DecisionEngine Engine = new();

    private static readonly char[] PathSeparators = ['\\', '/'];

    private static readonly IReadOnlyDictionary<long, Quality> Qualities =
        SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality);

    private static readonly QualityProfile Standard =
        SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId);

    private static readonly QualityProfile Lossless =
        SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.LosslessProfileId);

    private static readonly ParsedName GoodParse =
        new("Daft Punk", "Get Lucky", "Random Access Memories", 8, VersionFlags.None, [], VersionFlags.None, [], false);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1000, 90)]
    [InlineData(2000, 82)]
    [InlineData(3000, 74)]
    [InlineData(3100, 0)]
    public void Scores_the_duration_component_from_the_difference(int differenceMs, int expected)
    {
        var decision = Judge(Context(), NewCandidate(durationMs: 369_000 + differenceMs));

        decision.Score.Duration.Should().Be(expected);
    }

    [Fact]
    public void Scores_40_for_the_duration_component_when_either_duration_is_unknown()
    {
        Judge(Context(), NewCandidate(durationMs: null)).Score.Duration.Should().Be(40);
        Judge(Context() with { SongDurationMs = null }, NewCandidate()).Score.Duration.Should().Be(40);
    }

    [Fact]
    public void Scores_300_for_a_quality_at_or_above_the_cutoff()
    {
        Judge(Context(), NewCandidate(qualityId: 29)).Score.Quality.Should().Be(300);
        Judge(Context(), NewCandidate(qualityId: 36)).Score.Quality.Should().Be(300);
    }

    [Fact]
    public void Scores_240_for_a_quality_one_allowed_group_below_the_cutoff()
    {
        var context = Context() with { Profile = Lossless };

        // The Lossless profile allows "High lossy" (MP3-320) but not the group between it and the
        // cutoff, so one allowed group — and 60 points — sits below.
        Judge(context, NewCandidate(qualityId: 29)).Score.Quality.Should().Be(240);
    }

    [Fact]
    public void Scores_150_for_a_free_slot_an_empty_queue_and_a_fast_upload()
    {
        var candidate = NewCandidate(availability: new CandidateAvailability(true, 0, 2_000_000));

        Judge(Context(), candidate).Score.Availability.Should().Be(150);
    }

    [Fact]
    public void Clamps_the_adjustment_total_to_50_in_each_direction()
    {
        var generous = Context() with
        {
            Reputation = new Dictionary<string, UserReputation>(StringComparer.OrdinalIgnoreCase)
            {
                ["peer1"] = new(Successes: 2, Failures: 0, FailuresLast24Hours: 0),
            },
        };

        Judge(generous, NewCandidate()).Score.AdjustmentTotal.Should().Be(50);

        var punished = Context() with
        {
            Reputation = new Dictionary<string, UserReputation>(StringComparer.OrdinalIgnoreCase)
            {
                ["peer1"] = new(Successes: 0, Failures: 2, FailuresLast24Hours: 0),
            },
        };

        var sloppy = NewCandidate(parsed: GoodParse with
        {
            Album = null,
            TrackNo = null,
            HasUnexplainedBrackets = true,
        });

        Judge(punished, sloppy).Score.AdjustmentTotal.Should().Be(-50);
    }

    [Fact]
    public void Keeps_the_total_between_0_and_1000()
    {
        Judge(Context(), NewCandidate()).Score.Total.Should().BeInRange(0, 1000);

        var hopeless = Context() with { SourceTier = 3, SongFlags = VersionFlags.Live };
        var nothing = NewCandidate(
            qualityId: 999,
            durationMs: null,
            extension: "lrc",
            sizeBytes: 4_794,
            availability: new CandidateAvailability(false, 40, 1_000),
            parsed: GoodParse with { Artist = null, Title = null, Album = null, TrackNo = null });

        Judge(hopeless, nothing).Score.Total.Should().BeInRange(0, 1000);
    }

    [Fact]
    public void Caps_the_total_when_a_duration_is_unknown()
    {
        var decision = Judge(Context(), NewCandidate(durationMs: null));

        decision.Score.CappedForUnknownDuration.Should().BeTrue();
        decision.Score.Total.Should().Be(DecisionEngine.UnknownDurationCap);
        Engine.IsGoodEnough(decision).Should().BeFalse();
    }

    [Fact]
    public void Orders_the_same_candidates_the_same_way_whatever_the_input_order()
    {
        var first = NewCandidate(remotePath: "@@a\\Music\\Daft Punk\\Random Access Memories\\08 Get Lucky.flac", qualityId: 36, sizeBytes: 43_600_000);
        var second = NewCandidate(remotePath: "@@b\\Music\\Daft Punk\\Random Access Memories\\08 Get Lucky.mp3", sizeBytes: 14_850_000);
        var third = NewCandidate(provider: "peer2", remotePath: "@@c\\Music\\Daft Punk\\Random Access Memories\\08 Get Lucky.mp3", sizeBytes: 14_700_000);

        var forwards = Engine.Evaluate(Context(), [first, second, third]).Select(Key);
        var backwards = Engine.Evaluate(Context(), [third, second, first]).Select(Key);
        var shuffled = Engine.Evaluate(Context(), [second, third, first]).Select(Key);

        forwards.Should().Equal(backwards);
        forwards.Should().Equal(shuffled);
    }

    [Fact]
    public void Never_returns_an_empty_rejection_or_adjustment_message()
    {
        var context = Context() with
        {
            CurrentFileQualityId = 29,
            SongFlags = VersionFlags.Live,
            IgnoredUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "peer1" },
            Reputation = new Dictionary<string, UserReputation>(StringComparer.OrdinalIgnoreCase)
            {
                ["peer1"] = new(Successes: 0, Failures: 3, FailuresLast24Hours: 3),
            },
        };

        var candidate = NewCandidate(
            qualityId: 13,
            durationMs: 60_000,
            extension: "lrc",
            sizeBytes: 4_794,
            isLocked: true,
            parsed: GoodParse with { Artist = "Someone Else", Title = "Something Else" });

        var decision = Judge(context, candidate);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Should().HaveCountGreaterThan(5);
        decision.Rejections.Should().OnlyContain(rejection => !string.IsNullOrWhiteSpace(rejection.Message));
        decision.Rejections.Should().OnlyContain(rejection => !string.IsNullOrWhiteSpace(rejection.Reason.ToWireName()));
        decision.Score.Adjustments.Should().OnlyContain(adjustment => !string.IsNullOrWhiteSpace(adjustment.Name));
    }

    [Fact]
    public void Names_every_rejection_reason_in_camel_case()
    {
        RejectionReason.DurationOutOfTolerance.ToWireName().Should().Be("durationOutOfTolerance");
        RejectionReason.NotAnUpgrade.ToWireName().Should().Be("notAnUpgrade");
        RejectionReason.BelowMinimumScore.ToWireName().Should().Be("belowMinimumScore");
    }

    private static CandidateDecision Judge(DecisionContext context, Candidate candidate) =>
        Engine.Evaluate(context, [candidate]).Single();

    private static string Key(CandidateDecision decision) => decision.Candidate.BlocklistKey;

    private static DecisionContext Context() => new()
    {
        SongTitle = "Get Lucky",
        MainArtists = ["Daft Punk"],
        SongDurationMs = 369_000,
        AlbumTitle = "Random Access Memories",
        TrackNo = 8,
        Profile = Standard,
        Qualities = Qualities,
    };

    private static Candidate NewCandidate(
        long qualityId = 29,
        int? durationMs = 369_000,
        string provider = "peer1",
        string? extension = "mp3",
        long? sizeBytes = 14_850_000,
        string remotePath = DefaultRemotePath,
        CandidateAvailability? availability = null,
        bool isLocked = false,
        ParsedName? parsed = null) => new()
        {
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.Soulseek(provider, remotePath),
            DisplayName = remotePath[(remotePath.LastIndexOfAny(PathSeparators) + 1)..],
            RemotePath = remotePath,
            Provider = provider,
            QualityId = qualityId,
            Extension = extension,
            DurationMs = durationMs,
            SizeBytes = sizeBytes,
            IsLocked = isLocked,
            Availability = availability ?? new CandidateAvailability(true, 0, 2_000_000),
            Parsed = parsed ?? GoodParse,
        };
}
