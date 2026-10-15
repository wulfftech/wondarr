using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Profiles;
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

        var hopeless = Context() with { SongFlags = VersionFlags.Live };
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
    public void A_usenet_post_over_the_limit_is_rejected_only_while_it_downloads_whole()
    {
        var unknownList = UnlistedContainer(SourceTypes.Newznab, size: 2_000L * 1024 * 1024);
        var matched = unknownList with
        {
            Extension = "flac",
            Release = unknownList.Release! with { FileIndex = 2 },
        };

        // A RAR set or an obfuscated post downloads whole: the limit holds.
        Judge(Context(), unknownList).Rejections
            .Should().ContainSingle(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);

        // A clean post is trimmed to the wanted file by SABnzbd, so its size says nothing about the download.
        Judge(Context(), matched).Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);
    }

    [Theory]
    [InlineData(SourceTypes.Newznab)]
    [InlineData(SourceTypes.Torznab)]
    public void A_container_whose_file_list_is_unknown_is_not_rejected_as_not_audio(string sourceType)
    {
        var candidate = UnlistedContainer(sourceType, size: 500L * 1024 * 1024);

        var decision = Judge(Context(), candidate);

        decision.Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.NotAudio);
        decision.Accepted.Should().BeTrue("a release whose file list is unknown stays a candidate; its file is found after the grab");
    }

    [Fact]
    public void An_unknown_file_list_still_meets_every_other_rule()
    {
        var blocklisted = Context() with { IsBlocklisted = key => key == "release-key" };
        var unknownQuality = UnlistedContainer(SourceTypes.Newznab) with { QualityId = 1 };
        var noSeeders = UnlistedContainer(SourceTypes.Torznab) with { Availability = new CandidateAvailability(Seeders: 0) };
        var tooLarge = UnlistedContainer(SourceTypes.Newznab, size: 2_000L * 1024 * 1024);

        // Each one fails its own rule and is not turned away as not audio.
        var blocklistedDecision = Judge(blocklisted, UnlistedContainer(SourceTypes.Newznab));
        blocklistedDecision.Rejections.Select(rejection => rejection.Reason).Should().Equal(RejectionReason.Blocklisted);

        var unknownQualityDecision = Judge(Context(), unknownQuality);
        unknownQualityDecision.Rejections.Select(rejection => rejection.Reason).Should().Equal(RejectionReason.FormatNotAllowed);

        var noSeedersDecision = Judge(Context(), noSeeders);
        noSeedersDecision.Rejections.Select(rejection => rejection.Reason).Should().Equal(RejectionReason.NoSeeders);

        var tooLargeDecision = Judge(Context(), tooLarge);
        tooLargeDecision.Rejections.Select(rejection => rejection.Reason).Should().Equal(RejectionReason.ContainerTooLarge);
    }

    [Theory]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA", "Norah Jones", "Come Away with Me")]
    [InlineData("Norah_Jones-Come_Away_With_Me-JAPANESE_RETAIL-2002-pH", "Norah Jones", "Come Away With Me")]
    [InlineData("Norah Jones - Come Away With Me (2002) [FLAC]", "Norah Jones", "Come Away With Me")]
    [InlineData("Norah.Jones-Dont.Know.Why-SINGLE-FLAC-2002-GRP", "Norah Jones", "Dont Know Why")]
    public void An_unknown_file_list_of_the_songs_artist_and_album_is_accepted(string title, string artist, string album)
    {
        var decision = Judge(NorahContext(), UnlistedContainer(SourceTypes.Newznab, title, artist, album));

        decision.Rejections.Should().BeEmpty();
        decision.Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_hyphenated_artist_the_release_name_parsed_wrong_is_still_the_artist_when_the_name_starts_with_it()
    {
        var context = NorahContext() with { SongTitle = "Take On Me", MainArtists = ["a-ha"], AlbumTitle = "Hunting High and Low" };
        var candidate = UnlistedContainer(SourceTypes.Newznab, "A-ha-Hunting_High_and_Low-CD-FLAC-1985-GRP", "A", "ha-Hunting High and Low");

        var decision = Judge(context, candidate);

        decision.Rejections.Should().BeEmpty();
        decision.Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("Tom.Jones-Greatest.Hits-CD-FLAC-1999-GRP", "Tom Jones", "Greatest Hits", RejectionReason.ArtistMismatch)]
    [InlineData("Jones-Whatever-CD-FLAC-2002-GRP", "Jones", "Whatever", RejectionReason.ArtistMismatch)]
    [InlineData("Norah.Jones.and.Friends-Come.Away.with.Me-CD-FLAC-2002-GRP", "Norah Jones and Friends", "Come Away with Me", RejectionReason.ArtistMismatch)]
    [InlineData("VA-Now.Thats.What.I.Call.Music-2CD-FLAC-2002-GRP", "VA", "Now Thats What I Call Music", RejectionReason.ArtistMismatch)]
    [InlineData("Various.Artists-Come.Away.with.Me-CD-FLAC-2002-GRP", "Various Artists", "Come Away with Me", RejectionReason.ArtistMismatch)]
    [InlineData("Norah.Jones-Dont.Know.Why-OST-FLAC-2002-GRP", "Norah Jones", "Dont Know Why", RejectionReason.ArtistMismatch)]
    [InlineData("Norah.Jones-Feels.Like.Home-CD-FLAC-2004-GRP", "Norah Jones", "Feels Like Home", RejectionReason.TitleMismatch)]
    [InlineData("Norah.Jones-Discography-1997-2013-FLAC-GRP", "Norah Jones", "Discography", RejectionReason.TitleMismatch)]
    public void An_unknown_file_list_that_is_not_the_songs_artist_and_album_is_rejected(
        string title,
        string artist,
        string album,
        RejectionReason expected)
    {
        var decision = Judge(NorahContext(), UnlistedContainer(SourceTypes.Newznab, title, artist, album));

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(expected);
    }

    [Fact]
    public void An_unknown_file_list_is_judged_the_same_for_a_torrent()
    {
        Judge(NorahContext(), UnlistedContainer(SourceTypes.Torznab, "Tom.Jones-Greatest.Hits-CD-FLAC-1999-GRP", "Tom Jones", "Greatest Hits"))
            .Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);
        Judge(NorahContext(), UnlistedContainer(SourceTypes.Torznab, "Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA", "Norah Jones", "Come Away with Me"))
            .Accepted.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_file_list_keeps_its_lower_availability_score()
    {
        var unlisted = UnlistedContainer(SourceTypes.Newznab);
        var listed = unlisted with { Availability = unlisted.Availability with { FileListKnown = true } };

        Judge(Context(), listed).Score.Availability.Should().Be(Judge(Context(), unlisted).Score.Availability + 30);
    }

    [Theory]
    [InlineData(SourceTypes.Soulseek, null)]
    [InlineData(SourceTypes.Soulseek, "lrc")]
    [InlineData(SourceTypes.YouTube, null)]
    [InlineData(SourceTypes.YouTube, "webm")]
    public void A_single_file_without_an_audio_extension_is_still_not_audio(string sourceType, string? extension)
    {
        var candidate = NewCandidate(extension: extension) with { SourceType = sourceType };

        Judge(Context(), candidate).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.NotAudio);
    }

    [Fact]
    public void A_container_whose_matched_file_is_not_audio_is_still_not_audio()
    {
        var matched = UnlistedContainer(SourceTypes.Newznab) with { Release = UnlistedContainer(SourceTypes.Newznab).Release! with { FileIndex = 3 } };

        // The matcher only ever chooses audio, so a file that is named but has no audio extension is a fault.
        Judge(Context(), matched with { Extension = "nfo" }).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.NotAudio);
        Judge(Context(), matched with { Extension = null }).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.NotAudio);
    }

    [Fact]
    public void A_single_file_candidate_with_a_release_but_no_container_is_still_not_audio()
    {
        // The exemption is for a container, not for any candidate that happens to carry a release.
        var candidate = UnlistedContainer(SourceTypes.Newznab) with { Container = CandidateContainer.SingleFile };

        Judge(Context(), candidate).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.NotAudio);
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
    public void A_compilation_path_needs_the_artist_in_the_file_name_or_the_full_title()
    {
        const string message = "Artist mismatch: Various Artists path without the artist or the full title.";

        // Neither the artist nor the song: rejected, with the compilation message.
        var unrelated = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Other Song.mp3",
            parsed: ParsedName.Empty with { Title = "Other Song", TrackNo = 5 });

        Judge(Context(), unrelated).Rejections.Should().Contain(new Rejection(RejectionReason.ArtistMismatch, message));

        // A close but not full title match ("Get Lucy Star") is not enough either.
        var close = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Get Lucy Star.mp3",
            parsed: ParsedName.Empty with { Title = "Get Lucy Star", TrackNo = 5 });

        Judge(Context(), close).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);

        // The song's own title in full: passes.
        var song = NewCandidate(
            remotePath: @"@@b\Various Artists\Hits 2013\05 Get Lucky.mp3",
            parsed: ParsedName.Empty with { Title = "Get Lucky", TrackNo = 5 });

        Judge(Context(), song).Rejections.Should().BeEmpty();
    }

    [Fact]
    public void A_compilation_path_with_one_word_of_the_artist_in_a_folder_is_not_the_artist()
    {
        var context = Context() with { SongTitle = "Story of a Girl", MainArtists = ["Nine Days"] };

        // "Days" is a folder token of the artist's name, and the title overlaps without being the song.
        var partial = NewCandidate(
            remotePath: @"@@b\Various Artists\Days of Thunder OST\05 Story of a Dream.mp3",
            parsed: ParsedName.Empty with { Title = "Story of a Dream", TrackNo = 5 });

        Judge(context, partial).Rejections.Should().ContainSingle()
            .Which.Should().Be(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: Various Artists path without the artist or the full title."));
    }

    [Fact]
    public void A_compilation_path_needs_a_full_title_when_a_folder_only_holds_the_artist_among_other_words()
    {
        var context = Context() with { SongTitle = "Story of a Girl", MainArtists = ["Nine Days"] };

        // A Various Artists folder never stands in for the artist, and neither does "Nine Days Tribute".
        var folderOnly = NewCandidate(
            remotePath: @"@@b\Various Artists\Nine Days Tribute\05 Story of a Dream.mp3",
            parsed: ParsedName.Empty with { Title = "Story of a Dream", TrackNo = 5 });

        Judge(context, folderOnly).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);
    }

    [Theory]
    [InlineData("12 Story of a Girl (Remastered).mp3", VersionFlags.Remaster)]
    [InlineData("12 Story of a Girl [Explicit].mp3", VersionFlags.Explicit)]
    public void A_compilation_path_passes_on_a_full_title_with_a_soft_marker(string fileName, VersionFlags flags)
    {
        var context = Context() with { SongTitle = "Story of a Girl", MainArtists = ["Nine Days"] };
        var parsed = ParsedName.Empty with { Title = "Story of a Girl", TrackNo = 12, VersionFlags = flags };

        Judge(context, NewCandidate(remotePath: @"@@b\Various Artists\Now 48\" + fileName, parsed: parsed))
            .Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);
    }

    [Fact]
    public void A_whole_artist_folder_above_a_soundtrack_album_is_the_artist()
    {
        var context = Context() with { SongTitle = "Time", MainArtists = ["Hans Zimmer"] };
        var parsed = ParsedName.Empty with { Title = "Time (From Inception)", TrackNo = 5 };

        Judge(context, NewCandidate(remotePath: @"@@b\Hans Zimmer\Inception OST\05 Time (From Inception).mp3", parsed: parsed))
            .Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);

        // The same album under a folder that is not the artist needs the full title, which this is not.
        Judge(context, NewCandidate(remotePath: @"@@b\Soundtracks\Inception OST\05 Time (From Inception).mp3", parsed: parsed))
            .Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.ArtistMismatch);
    }

    [Fact]
    public void A_compilation_path_passes_on_a_full_title_and_scores_below_a_file_in_the_artists_folder()
    {
        var context = Context() with { SongTitle = "Story of a Girl", MainArtists = ["Nine Days"] };
        var parsed = ParsedName.Empty with { Title = "Story of a Girl", TrackNo = 12 };

        var compilation = Judge(context, NewCandidate(remotePath: @"@@b\Various Artists\Now 48\12 Story of a Girl.mp3", parsed: parsed));
        var album = Judge(context, NewCandidate(remotePath: @"@@b\Nine Days\The Madding Crowd\12 Story of a Girl.mp3", parsed: parsed));

        compilation.Rejections.Should().BeEmpty();
        album.Rejections.Should().BeEmpty();
        compilation.Score.Total.Should().BeLessThan(album.Score.Total);
    }

    [Fact]
    public void A_folder_token_of_the_artist_on_a_compilation_path_does_not_lift_the_title_floor_for_another_script()
    {
        var context = Context() with { SongTitle = "Story of a Girl", MainArtists = ["Nine Days"] };

        // The Korean title would be waved through as a transliteration if "Days" in a folder counted as the artist.
        var candidate = NewCandidate(
            remotePath: @"@@b\Various Artists\Days of Thunder OST\09 - 사랑이 온거야.mp3",
            parsed: ParsedName.Empty with { Title = "사랑이 온거야", TrackNo = 9 });

        Judge(context, candidate).Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.TitleMismatch);
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

    [Theory]
    [InlineData("ape")]
    [InlineData("wv")]
    [InlineData("wma")]
    public void Rejects_a_container_the_tag_writer_cannot_handle(string extension)
    {
        var decision = Judge(Context(), NewCandidate(qualityId: 36, extension: extension));

        decision.Rejections.Should().Contain(rejection => rejection.Reason == RejectionReason.UntaggableFormat);
        decision.Rejections.Single(rejection => rejection.Reason == RejectionReason.UntaggableFormat)
            .Message.Should().Contain($".{extension}");
        decision.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Accepts_an_ape_when_the_librarys_lossless_rule_converts_it()
    {
        var context = Context() with
        {
            Profile = Lossless,
            OutputPolicy = LibraryOutputPolicy.Parse("""{"version":2,"lossless":{"codec":"flac"}}"""),
        };

        Judge(context, NewCandidate(qualityId: 36, extension: "ape")).Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.UntaggableFormat);
    }

    [Fact]
    public void Rejects_an_ape_when_the_librarys_rule_keeps_lossless_files()
    {
        var context = Context() with
        {
            Profile = Lossless,
            OutputPolicy = LibraryOutputPolicy.Parse("""{"version":2,"lossless":{"codec":"keep"}}"""),
        };

        Judge(context, NewCandidate(qualityId: 36, extension: "ape")).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.UntaggableFormat);
    }

    [Fact]
    public void A_lossy_rule_does_not_save_a_lossless_container()
    {
        // The rules are per source class: a lossy rule that converts says nothing about an APE.
        var context = Context() with
        {
            Profile = Lossless,
            OutputPolicy = LibraryOutputPolicy.Parse("""{"version":2,"lossy":{"codec":"mp3","bitrateKbps":320}}"""),
        };

        Judge(context, NewCandidate(qualityId: 36, extension: "ape")).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.UntaggableFormat);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    [InlineData("opus")]
    [InlineData("ogg")]
    [InlineData("wav")]
    public void Does_not_reject_a_container_the_tag_writer_handles(string extension)
    {
        Judge(Context(), NewCandidate(extension: extension)).Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.UntaggableFormat);
    }

    /// <summary>
    /// What ContainerCandidates builds for a magnet, a RAR set or an obfuscated post: the release itself,
    /// named after the artist, with no extension and no file index.
    /// </summary>
    private static Candidate UnlistedContainer(string sourceType, long size = 400L * 1024 * 1024) => new()
    {
        SourceType = sourceType,
        BlocklistKey = "release-key",
        DisplayName = "Daft Punk-Random Access Memories-CD-FLAC-2013-GRP",
        RemotePath = "Daft Punk-Random Access Memories-CD-FLAC-2013-GRP",
        Provider = "Indexer",
        Parsed = ParsedName.Empty with { Artist = "Daft Punk", Album = "Random Access Memories" },
        QualityId = 36,
        Container = CandidateContainer.AlbumContainer,
        Availability = new CandidateAvailability(Seeders: 10, AgeDays: 100, FileListKnown: false),
        Release = new ContainerRelease
        {
            IndexerId = 1,
            IndexerName = "Indexer",
            Title = "Daft Punk-Random Access Memories-CD-FLAC-2013-GRP",
            ReleaseId = "guid-1",
            Size = size,
        },
    };

    /// <summary>A release with an unknown file list, named as an indexer lists it and parsed as the search parses it.</summary>
    private static Candidate UnlistedContainer(string sourceType, string title, string artist, string album) =>
        UnlistedContainer(sourceType) with
        {
            DisplayName = title,
            RemotePath = title,
            Parsed = ParsedName.Empty with { Artist = artist, Album = album },
            Release = UnlistedContainer(sourceType).Release! with { Title = title },
        };

    /// <summary>The song of "Come Away with Me": Norah Jones, "Don’t Know Why".</summary>
    private static DecisionContext NorahContext() => Context() with
    {
        SongTitle = "Don’t Know Why",
        MainArtists = ["Norah Jones"],
        SongDurationMs = 186_000,
        AlbumTitle = "Come Away with Me",
        TrackNo = 1,
    };

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
