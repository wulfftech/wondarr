using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>The source tiers and the YouTube video-type adjustments of MATCHING_ENGINE §6.3 (R-070).</summary>
public class SourceRankingTests
{
    private static readonly DecisionEngine Engine = new();

    private static readonly ParsedName Parse =
        new("Daft Punk", "Get Lucky", "Random Access Memories", 8, VersionFlags.None, [], VersionFlags.None, [], false);

    private static readonly QualityProfile Standard =
        SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId);

    [Fact]
    public void At_equal_quality_soulseek_outranks_youtube_which_outranks_a_torrent()
    {
        var soulseek = Candidate(SourceTypes.Soulseek, new CandidateAvailability(true, 0));
        var youtube = Candidate(SourceTypes.YouTube, new CandidateAvailability(true, 0));
        var torrent = Candidate(SourceTypes.Torznab, new CandidateAvailability(Seeders: 10, Freeleech: true));

        var decisions = Engine.Evaluate(Context(Standard), [torrent, youtube, soulseek]);

        decisions.Select(decision => decision.Score.SourcePreference).Should().Equal(100, 60, 30);
        decisions.Select(decision => decision.Candidate.SourceType)
            .Should().Equal(SourceTypes.Soulseek, SourceTypes.YouTube, SourceTypes.Torznab);
        decisions.Select(decision => decision.Score.Availability).Should().OnlyContain(value => value == 120);
        DecisionEngine.SourceTierOf(SourceTypes.Newznab).Should().Be(3);
    }

    [Fact]
    public void An_art_track_beats_an_official_video_and_a_user_upload_ranks_last()
    {
        var atv = YouTube("atv", YouTubeVideoKinds.ArtTrack);
        var omv = YouTube("omv", YouTubeVideoKinds.OfficialVideo);
        var ugc = YouTube("ugc", YouTubeVideoKinds.UserUpload);

        var decisions = Engine.Evaluate(Context(Standard), [ugc, omv, atv]);

        decisions.Select(decision => decision.Candidate.RemotePath).Should().Equal("atv", "omv", "ugc");

        static int Points(CandidateDecision decision, string name) =>
            decision.Score.Adjustments.Single(adjustment => adjustment.Name == name).Points;

        Points(decisions[0], "artTrack").Should().Be(40);
        Points(decisions[1], "officialVideo").Should().Be(-20);
        Points(decisions[2], "userUpload").Should().Be(-40);
    }

    [Fact]
    public void Min_score_judges_each_source_by_its_own_total()
    {
        var soulseek = Candidate(SourceTypes.Soulseek, new CandidateAvailability(true, 0));
        var torrent = Candidate(SourceTypes.Torznab, new CandidateAvailability(Seeders: 10, Freeleech: true));

        var probe = Engine.Evaluate(Context(Standard), [soulseek]).Single().Score.Total;
        var strict = new QualityProfile
        {
            Name = "strict",
            Items = Standard.Items,
            CutoffQualityId = Standard.CutoffQualityId,
            DurationToleranceMs = Standard.DurationToleranceMs,
            MinScore = probe,
        };

        var decisions = Engine.Evaluate(Context(strict), [soulseek, torrent]);

        decisions.Single(decision => decision.Candidate.SourceType == SourceTypes.Soulseek).Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.BelowMinimumScore);
        decisions.Single(decision => decision.Candidate.SourceType == SourceTypes.Torznab).Rejections
            .Should().Contain(rejection => rejection.Reason == RejectionReason.BelowMinimumScore);
    }

    private static Candidate YouTube(string videoId, string kind) =>
        Candidate(SourceTypes.YouTube, new CandidateAvailability(true, 0)) with
        {
            VideoKind = kind,
            RemotePath = videoId,
            BlocklistKey = "youtube:" + videoId,
        };

    private static Candidate Candidate(string sourceType, CandidateAvailability availability) => new()
    {
        SourceType = sourceType,
        BlocklistKey = sourceType + ":key",
        DisplayName = "Get Lucky",
        RemotePath = "Daft Punk\\Random Access Memories\\08 Get Lucky.mp3",
        Provider = "peer1",
        QualityId = 29,
        Extension = "mp3",
        DurationMs = 369_000,
        SizeBytes = 14_850_000,
        Availability = availability,
        Parsed = Parse,
    };

    private static DecisionContext Context(QualityProfile profile) => new()
    {
        SongTitle = "Get Lucky",
        MainArtists = ["Daft Punk"],
        SongDurationMs = 369_000,
        AlbumTitle = "Random Access Memories",
        TrackNo = 8,
        Profile = profile,
        Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
    };
}
