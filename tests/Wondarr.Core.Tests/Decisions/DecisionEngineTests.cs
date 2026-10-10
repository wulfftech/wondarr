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
        RejectionReason.WorseIdentity.ToWireName().Should().Be("worseIdentity");
    }

    [Fact]
    public void Rejects_an_upgrade_whose_identity_score_is_below_the_current_files()
    {
        // A held MP3-256 whose candidate matched the song at 390, and a FLAC that matches it clearly
        // less well (more than the tolerance below): the quality rule alone would take it, the
        // identity rule must not.
        var context = Context() with { CurrentFileQualityId = 23, CurrentFileIdentityScore = 390 };

        var worse = NewCandidate(qualityId: 36, sizeBytes: 43_600_000, durationMs: 371_000, parsed: GoodParse with { Title = "Get Lucky Star" });

        Judge(context, worse).Accepted.Should().BeFalse();
        Judge(context, worse).Rejections.Should().ContainSingle()
            .Which.Reason.Should().Be(RejectionReason.WorseIdentity);

        // A candidate that matches at least as well as the held file's is still an upgrade.
        Judge(context, NewCandidate(qualityId: 36, sizeBytes: 43_600_000)).Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData(390)]
    [InlineData(400)]
    public void A_near_perfect_or_hard_hit_held_score_does_not_block_a_slightly_worse_match(int held)
    {
        // DECISIONS build session 7 #11: live, a held score no name-scored candidate could reach
        // rejected 978 of 978 candidates. A FLAC a little less well matched (here: a two-second
        // longer edit) is within the tolerance, and fingerprint confirmation does the rest.
        var context = Context() with { CurrentFileQualityId = 23, CurrentFileIdentityScore = held };

        var slightlyWorse = NewCandidate(qualityId: 36, sizeBytes: 43_600_000, durationMs: 371_000);

        Judge(context, slightlyWorse).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.WorseIdentity);
    }

    [Fact]
    public void The_identity_rule_never_applies_without_a_known_held_score_or_to_a_manual_grab()
    {
        var worse = NewCandidate(qualityId: 36, sizeBytes: 43_600_000, parsed: GoodParse with { Title = "Get Lucky Star" });

        // No stored candidate behind the held file (adopted, or imported before scores were kept):
        // the quality rule is all there is.
        Judge(Context() with { CurrentFileQualityId = 23 }, worse).Accepted.Should().BeTrue();

        // The user asked for this one by hand: their decision, not the engine's.
        Judge(Context() with
        {
            CurrentFileQualityId = 23,
            CurrentFileIdentityScore = 390,
            IsManualGrab = true,
        }, worse).Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_torrent_scores_its_seeders_freeleech_and_known_file_list()
    {
        var torrent = NewCandidate() with
        {
            SourceType = SourceTypes.Torznab,
            Availability = new CandidateAvailability(Seeders: 10, Freeleech: true, FileListKnown: true),
        };

        Judge(Context(), torrent).Score.Availability.Should().Be(150);
        Judge(Context(), torrent with { Availability = new CandidateAvailability(Seeders: 1) })
            .Score.Availability.Should().Be(30);
    }

    [Fact]
    public void A_torrent_without_seeders_is_rejected()
    {
        var torrent = NewCandidate() with
        {
            SourceType = SourceTypes.Torznab,
            Availability = new CandidateAvailability(Seeders: 0),
        };

        Judge(Context(), torrent).Rejections.Should().ContainSingle(rejection => rejection.Reason == RejectionReason.NoSeeders);
    }

    [Fact]
    public void A_usenet_post_over_the_container_limit_is_rejected_and_a_torrent_is_not()
    {
        var release = new ContainerRelease { IndexerId = 1, IndexerName = "nzb", Title = "Album", ReleaseId = "g", Size = 2_000L * 1024 * 1024 };
        var post = NewCandidate() with { SourceType = SourceTypes.Newznab, Release = release, Availability = new CandidateAvailability(AgeDays: 100) };
        var torrent = post with { SourceType = SourceTypes.Torznab, Availability = new CandidateAvailability(Seeders: 5) };

        Judge(Context(), post).Rejections.Should().ContainSingle(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);
        Judge(Context() with { MaxContainerSizeBytes = 3_000L * 1024 * 1024 }, post).Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);
        Judge(Context(), torrent).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);
        Judge(Context(), post).Score.Availability.Should().Be(90);
    }

    [Fact]
    public void A_different_title_is_rejected_with_the_file_title_in_the_message()
    {
        var candidate = NewCandidate(
            remotePath: @"@@b\Music\Daft Punk\Random Access Memories\08 Instant Crush.mp3",
            parsed: GoodParse with { Title = "Instant Crush" });

        Judge(Context(), candidate).Rejections.Should().ContainSingle()
            .Which.Should().Be(new Rejection(
                RejectionReason.TitleMismatch,
                "Title mismatch: 'Instant Crush' does not match 'Get Lucky'."));
    }

    [Fact]
    public void A_title_failing_the_floor_is_rejected_before_the_identity_comparison_matters()
    {
        // A held file with a known identity score, an upgrade in quality, and a different song:
        // the title floor names the real problem, and the candidate is not accepted.
        var context = Context() with { CurrentFileQualityId = 23, CurrentFileIdentityScore = 390 };
        var other = NewCandidate(
            qualityId: 36,
            sizeBytes: 43_600_000,
            remotePath: @"@@b\Music\Daft Punk\Random Access Memories\08 Instant Crush.flac",
            parsed: GoodParse with { Title = "Instant Crush" });

        var decision = Judge(context, other);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
    }

    [Fact]
    public void The_title_floor_stands_down_for_another_script_only_with_the_artist_and_the_length()
    {
        var context = Context() with { SongTitle = "残酷な天使のテーゼ" };
        var romanised = NewCandidate(
            remotePath: @"@@b\Music\Daft Punk\Album\01 Zankoku na Tenshi no Teze.mp3",
            parsed: GoodParse with { Title = "Zankoku na Tenshi no Teze" });

        Judge(context, romanised).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
        Judge(context, romanised with { DurationMs = 380_000 }).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
        Judge(context, romanised with { DurationMs = null }).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
        Judge(context with { MainArtists = ["Someone Else"] }, romanised).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
    }

    [Fact]
    public void A_title_just_above_the_floor_passes()
    {
        // "Get Lucky Star" holds "Get Lucky" as a run of words.
        var candidate = NewCandidate(parsed: GoodParse with { Title = "Get Lucky Star" });

        Judge(Context(), candidate).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
    }

    [Fact]
    public void A_youtube_candidate_is_judged_on_its_parsed_title_not_its_video_id()
    {
        var video = NewCandidate(remotePath: "4D7u5KF7SP8", extension: null) with { SourceType = SourceTypes.YouTube };

        Judge(Context(), video).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
        Judge(Context(), video with { Parsed = GoodParse with { Title = "Instant Crush" } }).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
    }

    [Fact]
    public void A_container_whose_file_list_is_unknown_has_no_title_to_judge()
    {
        var release = NewCandidate(
            remotePath: "Daft Punk - Random Access Memories [FLAC]",
            extension: null,
            parsed: ParsedName.Empty with { Artist = "Daft Punk" }) with
        {
            SourceType = SourceTypes.Torznab,
            Container = CandidateContainer.AlbumContainer,
            Availability = new CandidateAvailability(Seeders: 5),
        };

        Judge(Context(), release).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
    }

    [Fact]
    public void A_compilation_path_needs_the_artist_or_the_song_in_the_file_name()
    {
        const string message = "Artist mismatch: a compilation path, and the file name names neither the artist nor the song closely enough.";

        // Neither the artist nor the song: rejected, with the compilation message.
        var unrelated = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Other Song.mp3",
            parsed: ParsedName.Empty with { Title = "Other Song", TrackNo = 5 });

        Judge(Context(), unrelated).Rejections.Should().Contain(new Rejection(RejectionReason.ArtistMismatch, message));

        // A close but not strong title match ("Get Lucy Star") is not enough either.
        var close = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Get Lucy Star.mp3",
            parsed: ParsedName.Empty with { Title = "Get Lucy Star", TrackNo = 5 });

        Judge(Context(), close).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);

        // The song's own title: passes, as before.
        var song = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Get Lucky.mp3",
            parsed: ParsedName.Empty with { Title = "Get Lucky", TrackNo = 5 });

        Judge(Context(), song).Rejections.Should().BeEmpty();
    }

    [Fact]
    public void A_compilation_path_passes_when_the_file_name_names_the_artist()
    {
        // The parser read a different artist off the folders, so the overlap is 0; the file name has it.
        var candidate = NewCandidate(
            remotePath: @"@@b\OST\Movie\05 Daft Punk - Get Lucky Star.mp3",
            parsed: ParsedName.Empty with { Artist = "Movie", Title = "Get Lucky Star", TrackNo = 5 });

        Judge(Context(), candidate).Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);
    }

    [Fact]
    public void A_path_without_a_compilation_marker_keeps_the_ordinary_artist_message()
    {
        var candidate = NewCandidate(
            remotePath: @"@@b\Music\Phrenia\Covers\06 Phrenia - Get Lucky.mp3",
            parsed: GoodParse with { Artist = "Phrenia" });

        Judge(Context(), candidate).Rejections.Should().ContainSingle()
            .Which.Message.Should().Be("Artist mismatch: none of the song's artists appears in the candidate's path.");
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
