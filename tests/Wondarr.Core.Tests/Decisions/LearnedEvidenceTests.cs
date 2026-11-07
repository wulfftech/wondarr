using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>
/// What the fingerprint check teaches a song: the title of another recording it found in the song's place,
/// and how many compilation files passed on their title alone and were another recording. The paths are the
/// ones production grabbed over and over for Phil's "Please Come Tonight" and The Saddle Club's "Out on a
/// Limb" in November 2026.
/// </summary>
public sealed class LearnedEvidenceTests
{
    private static readonly DecisionEngine Engine = new();

    private static readonly IReadOnlyDictionary<long, Quality> Qualities =
        SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality);

    private static readonly QualityProfile Standard =
        SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId);

    [Theory]
    [InlineData("Please Come Out Tonight", "Please Come Tonight", "Please Come Out Tonight")]
    [InlineData("Please Come Out Tonight (Live)", "Please Come Tonight", "Please Come Out Tonight")]
    [InlineData("Believe Her", "Out on a Limb", "Believe Her")]
    public void Learns_the_title_of_another_song(string otherTitle, string songTitle, string learned) =>
        LearnedEvidence.TitleToLearn(otherTitle, songTitle).Should().Be(learned);

    [Theory]
    [InlineData("Out on a Limb", "Out on a Limb")]
    [InlineData("OUT ON A LIMB", "Out on a Limb")]
    [InlineData("Absolutely (Story of a Girl)", "Story of a Girl")]
    [InlineData("Story of a Girl", "Absolutely (Story of a Girl)")]
    [InlineData("Brand New Day (Radio Edit)", "Brand New Day")]
    [InlineData("Money (Can't Buy)", "Money Can't Buy")]
    [InlineData("Hugo's Song (The Wild Weird World of Dr. Goldfoot, 1965)", "Hugo’s Song")]
    [InlineData("  ", "Out on a Limb")]
    public void Learns_nothing_from_the_songs_own_title_in_another_form(string otherTitle, string songTitle) =>
        LearnedEvidence.TitleToLearn(otherTitle, songTitle).Should().BeNull();

    [Fact]
    public void Without_a_learned_title_the_other_song_passes_on_the_artists_first_name()
    {
        // The gap the learned title closes: "Phil" is all of the artist's name and in "Phil Collins", and every
        // word of "Please Come Tonight" is in "Please Come Out Tonight".
        Judge(PhilContext(), @"@@a\Music\Phil Collins\Both Sides (1993)\11 - Please Come Out Tonight.flac")
            .Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"@@a\Music\Phil Collins\Both Sides (1993)\11 - Please Come Out Tonight.flac")]
    [InlineData(@"@@b\music\Phil Collins\Love Songs - A Compilation… Old and New (2004)\Phil Collins - Love Songs - A Compilation… Old and New - 09 - Please Come Out Tonight.flac")]
    [InlineData(@"@@c\Music\Phil Collins\Both Sides\1-11 Please Come Out Tonight.flac")]
    [InlineData(@"@@d\other\Phil Collins\Phil Collins - Album - 1993 - Both Sides\0111 - Please Come Out Tonight.flac")]
    [InlineData(@"@@e\LOSSLESS Albums\Phil Collins\(2004) Love Songs [16Bit-44.1kHz]\09. Please Come out Tonight.flac")]
    [InlineData(@"@@f\Music\Phil Collins\Both Sides\11 Phil Collins - Please Come Out Tonight (Remastered).flac")]
    public void A_learned_title_blocklists_every_copy_titled_that(string remotePath)
    {
        var decision = Judge(PhilContext() with { OtherRecordingTitles = ["Please Come Out Tonight"] }, remotePath);

        decision.Rejections.Should().ContainSingle().Which.Should().Be(new Rejection(
            RejectionReason.Blocklisted,
            "Blocklisted: a file titled 'Please Come Out Tonight' was fingerprinted as another recording."));
    }

    [Theory]
    [InlineData(@"@@a\Music\Phil\Singles\Phil - Please Come Tonight.mp3")]
    [InlineData(@"@@a\Music\Phil\Please Come Tonight\01 Please Come Tonight.flac")]
    public void A_learned_title_leaves_the_songs_own_title_alone(string remotePath) =>
        Judge(PhilContext() with { OtherRecordingTitles = ["Please Come Out Tonight"] }, remotePath)
            .Accepted.Should().BeTrue();

    [Fact]
    public void A_live_copy_of_the_learned_title_is_refused_as_that_recording_too()
    {
        var decision = Judge(
            PhilContext() with { OtherRecordingTitles = ["Please Come Out Tonight"] },
            @"@@a\Music\Phil Collins\Serious Hits Live\05 - Please Come Out Tonight (Live).flac");

        decision.Rejections.Select(rejection => rejection.Reason).Should()
            .Contain([RejectionReason.Blocklisted, RejectionReason.VersionMismatch]);
    }

    [Theory]
    [InlineData(@"@@cffry\JAZZ\VA - The Keynote Jazz Collection 1941-1947\CD 10\22 Out On A Limb.flac")]
    [InlineData(@"@@ssroc\Music\Compilations\The New Sound 1945-1947\05 Out on a Limb.mp3")]
    [InlineData(@"@@wxryp\MÚSICAS\VA - 2005 - The Ultimate Jazz Archive\24.2_Lennie Tristano (1946-1949)\01_Out on a Limb.mp3")]
    public void A_compilation_file_passing_on_its_title_alone_is_refused_from_the_second_miss(string remotePath)
    {
        Judge(SaddleClubContext() with { CompilationMisses = 1 }, remotePath).Accepted.Should().BeTrue();

        var decision = Judge(SaddleClubContext() with { CompilationMisses = 2 }, remotePath);

        decision.Rejections.Should().ContainSingle().Which.Should().Be(new Rejection(
            RejectionReason.ArtistMismatch,
            "Artist mismatch: Various Artists path without the artist, and 2 such files were other recordings."));
    }

    [Theory]
    [InlineData(@"@@a\Various Artists\Kids Hits 2004\05 - The Saddle Club - Out on a Limb.mp3")]
    [InlineData(@"@@a\Compilations\The Saddle Club\Kids Hits 2004\05 Out on a Limb.mp3")]
    [InlineData(@"@@a\Music\The Saddle Club\Hello World\21 - Out on a Limb.mp3")]
    public void Compilation_misses_never_refuse_a_file_that_names_the_artist(string remotePath) =>
        Judge(SaddleClubContext() with { CompilationMisses = 5 }, remotePath).Accepted.Should().BeTrue();

    [Theory]
    [InlineData(@"@@cffry\JAZZ\VA - The Keynote Jazz Collection 1941-1947\CD 10\22 Out On A Limb.flac", true)]
    [InlineData(@"@@a\Various Artists\Kids Hits 2004\05 - The Saddle Club - Out on a Limb.mp3", false)]
    [InlineData(@"@@a\Compilations\The Saddle Club\Kids Hits 2004\05 Out on a Limb.mp3", false)]
    [InlineData(@"@@a\Music\Jazz\Lennie Tristano\01 Out on a Limb.mp3", false)]
    [InlineData(@"@@a\Various Artists\Jazz Gems\01 Out on a Limb Blues.mp3", false)]
    public void Tells_a_file_that_passes_the_artist_rule_on_its_title_alone(string remotePath, bool alone) =>
        DecisionEngine.PassesArtistOnTitleAlone(["The Saddle Club"], "Out on a Limb", VersionFlags.None, Candidate(remotePath))
            .Should().Be(alone);

    [Fact]
    public void Learned_keys_are_read_back_from_a_songs_blocked_keys()
    {
        var keys = new[]
        {
            BlocklistKeys.Soulseek("peer", @"Music\Phil Collins\11 - Please Come Out Tonight.flac"),
            BlocklistKeys.SoulseekFile(@"Music\Phil Collins\Both Sides\11 - Please Come Out Tonight.flac")!,
            BlocklistKeys.OtherTitle(" Please Come Out Tonight "),
            BlocklistKeys.OtherTitle("Believe Her"),
            BlocklistKeys.CompilationMiss(BlocklistKeys.Soulseek("a", @"VA\01 Out on a Limb.mp3")),
            BlocklistKeys.CompilationMiss(BlocklistKeys.Soulseek("b", @"VA\02 Out on a Limb.mp3")),
        };

        BlocklistKeys.OtherTitles(keys).Should().Equal("Believe Her", "Please Come Out Tonight");
        BlocklistKeys.CompilationMisses(keys).Should().Be(2);
        BlocklistKeys.OtherTitles([BlocklistKeys.Soulseek("learned", "other-title")]).Should().BeEmpty();
    }

    private static CandidateDecision Judge(DecisionContext context, string remotePath) =>
        Engine.Evaluate(context, [Candidate(remotePath)]).Single();

    private static Candidate Candidate(string remotePath) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = BlocklistKeys.Soulseek("peer", remotePath),
        DisplayName = remotePath[(remotePath.LastIndexOf('\\') + 1)..],
        RemotePath = remotePath,
        Provider = "peer",
        QualityId = 29,
        Extension = remotePath[(remotePath.LastIndexOf('.') + 1)..].ToLowerInvariant(),
        DurationMs = 150_000,
        SizeBytes = 8_000_000,
        Availability = new CandidateAvailability(true, 0, 2_000_000),
        Parsed = SoulseekFilenameParser.Parse(remotePath).Parsed,
    };

    private static DecisionContext PhilContext() => new()
    {
        SongTitle = "Please Come Tonight",
        MainArtists = ["Phil"],
        SongDurationMs = 150_000,
        Profile = Standard,
        Qualities = Qualities,
    };

    private static DecisionContext SaddleClubContext() => new()
    {
        SongTitle = "Out on a Limb",
        MainArtists = ["The Saddle Club"],
        SongDurationMs = 150_000,
        AlbumTitle = "Singles",
        AlbumKind = AlbumContextKind.PseudoSingles,
        Profile = Standard,
        Qualities = Qualities,
    };
}
