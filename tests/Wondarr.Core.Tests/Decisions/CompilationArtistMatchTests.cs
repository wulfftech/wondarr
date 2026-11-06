using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>
/// A full title match on a compilation path no longer passes when the file name itself names another
/// artist, and a file the import found to be a different recording is skipped from every other user
/// (review 2026-11-04, new-06).
/// </summary>
public sealed class CompilationArtistMatchTests
{
    private const string Learned = "Blocklisted: the same file from another user was a different recording.";

    private static readonly DecisionEngine Engine = new();

    private static readonly IReadOnlyDictionary<long, Quality> Qualities =
        SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality);

    private static readonly QualityProfile Standard =
        SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId);

    [Theory]
    [InlineData(@"@@b\Various Artists\Nuggets\Disc 1\18 The Cryan Shames - Sugar and Spice.flac", "The Cryan Shames")]
    [InlineData(@"@@b\Various Artists\Nuggets\06 Searchers, The - Sugar and Spice.mp3", "Searchers, The")]
    [InlineData(@"@@b\Various Artists\Hits\Some Band - Sugar and Spice.mp3", "Some Band")]
    public void Rejects_a_compilation_file_whose_name_is_another_artist(string remotePath, string named)
    {
        var decision = Judge(Context(), remotePath);

        decision.Rejections.Should().Contain(new Rejection(
            RejectionReason.ArtistMismatch,
            $"Artist mismatch: Various Artists path and the file names another artist ({named})."));
        decision.Accepted.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"@@b\Various Artists\Nuggets\Disc 1\1-18 Sugar And Spice.flac")]
    [InlineData(@"@@b\Various Artists\Kids\05 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\Various Artists\Kids\Sugar and Spice.mp3")]
    public void Passes_a_compilation_file_whose_name_has_no_artist_field(string remotePath) =>
        Judge(Context(), remotePath).Rejections.Should().BeEmpty();

    [Theory]
    [InlineData(@"@@b\Various Artists\Kids\03 Saddle Club - Sugar and Spice.mp3")]
    [InlineData(@"@@b\Various Artists\Kids\03 The Saddle Club - Sugar and Spice.mp3")]
    [InlineData(@"@@b\Various Artists\Kids\03 Saddle Club, The - Sugar and Spice.mp3")]
    [InlineData(@"@@b\Various Artists\Kids\03 The Saddle Club feat. Somebody Else - Sugar and Spice.mp3")]
    public void Passes_a_compilation_file_whose_name_is_one_of_the_songs_artists(string remotePath) =>
        Judge(Context(), remotePath).Rejections.Should().BeEmpty();

    [Fact]
    public void Passes_a_compilation_file_when_any_of_several_song_artists_is_named()
    {
        var context = Context() with { MainArtists = ["Somebody Else", "The Saddle Club"] };

        Judge(context, @"@@b\Various Artists\Kids\03 Saddle Club - Sugar and Spice.mp3").Rejections.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"@@b\Various Artists\Nuggets\Disc 1\18 VA - Sugar and Spice.flac")]
    [InlineData(@"@@b\Various Artists\Nuggets\Disc 1\18 Various Artists - Sugar and Spice.flac")]
    public void Passes_a_compilation_file_whose_artist_field_is_a_compilation_marker(string remotePath) =>
        Judge(Context(), remotePath).Rejections.Should().BeEmpty();

    [Fact]
    public void Passes_a_compilation_file_whose_artist_field_is_the_album_folder()
    {
        Judge(Context(), @"@@b\Various Artists\Kids Hits\Kids Hits - 05 - Sugar and Spice.mp3").Rejections.Should().BeEmpty();
        Judge(Context(), @"@@b\Various Artists\2004 - Kids Hits\Kids Hits - Sugar and Spice.mp3").Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Does_not_use_the_rule_for_a_name_in_reversed_order()
    {
        // "Sugar and Spice - The Cryan Shames": the field before the dash is the song's title, so the new
        // rule stays out. The parser reads "The Cryan Shames" as the title, which is no full match: the file
        // is turned away by the old message before and after.
        Judge(Context(), @"@@b\Various Artists\Nuggets\Sugar and Spice - The Cryan Shames.mp3")
            .Rejections.Should().ContainSingle().Which.Should().Be(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: Various Artists path without the artist or the full title."));
    }

    [Fact]
    public void Reads_the_file_name_and_not_the_folder_for_the_artist()
    {
        // The parser takes the artist from the folder above when the file name has none.
        const string Path = @"@@b\The Cryan Shames\Various Artists\18 Sugar and Spice.flac";

        SoulseekFilenameParser.Parse(Path).Parsed.Artist.Should().Be("The Cryan Shames");
        Judge(Context(), Path).Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Leaves_a_path_without_a_compilation_marker_alone()
    {
        // Rejected before and after, with the ordinary message: no artist of the song in the path.
        Judge(Context(), @"@@b\Music\Nuggets\18 The Cryan Shames - Sugar and Spice.flac").Rejections.Should().ContainSingle()
            .Which.Should().Be(new Rejection(
                RejectionReason.ArtistMismatch,
                "Artist mismatch: none of the song's artists appears in full in the candidate's path."));
    }

    [Fact]
    public void Rejects_a_soulseek_file_a_wrong_recording_was_learned_from_under_another_user()
    {
        var first = Candidate("userA", @"@@a\Music\Nuggets\CD1\18 Sugar and Spice.flac");
        var other = Candidate("userB", @"@@b\Deeper\Share\Nuggets\CD1\18 SUGAR AND SPICE.mp3");
        var learned = BlocklistKeys.SoulseekFile(first.RemotePath, "Sugar and Spice")!;
        var context = Context() with { IsBlocklisted = key => key == learned };

        var decision = Engine.Evaluate(context, [other]).Single();

        decision.Rejections.Should().ContainSingle(rejection => rejection.Reason == RejectionReason.Blocklisted)
            .Which.Message.Should().Be(Learned);
    }

    [Fact]
    public void Gives_a_candidate_one_blocklisted_rejection_when_its_own_key_is_blocked_too()
    {
        var candidate = Candidate("userB", @"@@b\Nuggets\CD1\18 Sugar and Spice.flac");
        var learned = BlocklistKeys.SoulseekFile(candidate.RemotePath)!;
        var context = Context() with { IsBlocklisted = key => key == candidate.BlocklistKey || key == learned };

        Engine.Evaluate(context, [candidate]).Single().Rejections
            .Count(rejection => rejection.Reason == RejectionReason.Blocklisted).Should().Be(1);
    }

    [Theory]
    [InlineData(@"@@a\Music\Nuggets\1-18 Sugar And Spice.flac", @"@@b\x\y\Nuggets\1-18 sugar and spice.MP3")]
    [InlineData(@"@@a\Music\Nuggets 2\18. Sugar & Spice.flac", @"@@b\Nuggets 2\18 Sugar and Spice.mp3")]
    public void Keys_a_file_by_folder_and_name_whatever_the_case_punctuation_and_extension(string left, string right)
    {
        var key = BlocklistKeys.SoulseekFile(left);

        key.Should().NotBeNull();
        BlocklistKeys.SoulseekFile(right).Should().Be(key);
    }

    [Fact]
    public void Keys_a_file_in_a_disc_folder_by_the_album_above_it_as_well()
    {
        var key = BlocklistKeys.SoulseekFile(@"@@a\Music\Nuggets Original Artyfacts\CD1\18 Sugar and Spice.flac");

        key.Should().Be(string.Join(
            BlocklistKeys.Separator,
            "soulseek-file",
            "nuggets original artyfacts/cd1",
            "18 sugar and spice"));
        BlocklistKeys.SoulseekFile(@"@@a\Music\Nuggets Original Artyfacts\Disc 2\18 Sugar and Spice.flac")
            .Should().NotBe(key);
        BlocklistKeys.SoulseekFile(@"@@a\Music\Other Album\CD 01\18 Sugar and Spice.flac")
            .Should().NotBe(BlocklistKeys.SoulseekFile(@"@@a\Music\Another Album\CD 01\18 Sugar and Spice.flac"));
    }

    [Fact]
    public void Learns_nothing_from_a_bare_title_or_a_file_without_a_folder()
    {
        BlocklistKeys.SoulseekFile(@"@@a\Music\Sugar and Spice.mp3", "Sugar and Spice").Should().BeNull();
        BlocklistKeys.SoulseekFile(@"@@a\Music\SUGAR & SPICE.flac", "Sugar and Spice").Should().BeNull();
        BlocklistKeys.SoulseekFile("18 Sugar and Spice.flac", "Sugar and Spice").Should().BeNull();
        BlocklistKeys.SoulseekFile(@"CD1\18 Sugar and Spice.flac", "Sugar and Spice").Should().BeNull();
    }

    [Fact]
    public void A_file_key_never_equals_a_user_and_path_key()
    {
        var file = BlocklistKeys.SoulseekFile(@"@@a\Music\Nuggets\18 Sugar and Spice.flac")!;

        file.Count(character => character == BlocklistKeys.Separator).Should().Be(2);
        BlocklistKeys.Soulseek("userA", @"@@a\Music\Nuggets\18 Sugar and Spice.flac")
            .Count(character => character == BlocklistKeys.Separator).Should().Be(1);
    }

    [Theory]
    [InlineData(@"@@b\Various\CD1 - 05 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\Track 05 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\Disc 1 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\Side A - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\Vol 2 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\Volume 2 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\OST\2005 - Sugar and Spice.mp3")]
    public void Passes_a_compilation_file_whose_prefix_is_a_disc_track_side_or_volume(string remotePath) =>
        Judge(Context(), remotePath).Rejections.Should().BeEmpty();

    [Theory]
    [InlineData(@"@@b\Various\VA - Kids Hits\Kids Hits - 05 - Sugar and Spice.mp3")]
    [InlineData(@"@@b\Various\Nuggets Original Artyfacts Box Set\Nuggets - 18 - Sugar and Spice.mp3")]
    public void Passes_a_compilation_file_whose_artist_field_is_made_of_words_of_the_album_folder(string remotePath) =>
        Judge(Context(), remotePath).Rejections.Should().BeEmpty();

    [Theory]
    [InlineData(@"@@a\The Saddle Club\Soundtrack\05 - Sugar and Spice.mp3", @"@@a\Other\Soundtrack\05 - Sugar and Spice.mp3")]
    [InlineData(@"@@a\Nuggets\FLAC\05 - Sugar and Spice.flac", @"@@a\Other Album\FLAC\05 - Sugar and Spice.flac")]
    [InlineData(@"@@a\Nuggets\Various Artists\05 - Sugar and Spice.flac", @"@@a\Other Album\VA\05 - Sugar and Spice.flac")]
    [InlineData(@"@@a\Nuggets\Music\05 - Sugar and Spice.flac", @"@@a\Other Album\Music\05 - Sugar and Spice.flac")]
    public void A_generic_parent_folder_takes_the_folder_above_it_into_the_key(string left, string right)
    {
        var leftKey = BlocklistKeys.SoulseekFile(left);

        leftKey.Should().NotBeNull();
        BlocklistKeys.SoulseekFile(right).Should().NotBeNull().And.NotBe(leftKey);
    }

    [Theory]
    [InlineData(@"Music\05 - Sugar and Spice.mp3")]
    [InlineData(@"@@a\Music\05 - Sugar and Spice.mp3")]
    [InlineData(@"@@a\Soundtrack\05 - Sugar and Spice.mp3")]
    [InlineData(@"@@a\Music\FLAC\05 - Sugar and Spice.mp3")]
    public void A_generic_parent_folder_with_nothing_meaningful_above_it_gives_no_key(string remotePath) =>
        BlocklistKeys.SoulseekFile(remotePath).Should().BeNull();

    private static CandidateDecision Judge(DecisionContext context, string remotePath) =>
        Engine.Evaluate(context, [Candidate("peer", remotePath)]).Single();

    private static Candidate Candidate(string user, string remotePath) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = BlocklistKeys.Soulseek(user, remotePath),
        DisplayName = remotePath[(remotePath.LastIndexOf('\\') + 1)..],
        RemotePath = remotePath,
        Provider = user,
        QualityId = 29,
        Extension = remotePath[(remotePath.LastIndexOf('.') + 1)..].ToLowerInvariant(),
        DurationMs = 150_000,
        SizeBytes = 8_000_000,
        Availability = new CandidateAvailability(true, 0, 2_000_000),
        Parsed = SoulseekFilenameParser.Parse(remotePath).Parsed,
    };

    private static DecisionContext Context() => new()
    {
        SongTitle = "Sugar and Spice",
        MainArtists = ["The Saddle Club"],
        SongDurationMs = 150_000,
        AlbumTitle = "Singles",
        Profile = Standard,
        Qualities = Qualities,
    };
}
