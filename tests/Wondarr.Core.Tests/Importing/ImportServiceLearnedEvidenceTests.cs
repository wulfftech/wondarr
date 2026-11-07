using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// A file the fingerprint showed to be another recording teaches the song that recording's title, when it
/// is not the song's own, and counts a compilation miss when only its title had let it through. Production
/// grabbed Phil Collins' "Please Come Out Tonight" 80 times for Phil's "Please Come Tonight" before this.
/// </summary>
public sealed class ImportServiceLearnedEvidenceTests
{
    private const string OtherReason =
        "Fingerprint belongs to a different recording: Please Come Out Tonight (c80ad2a5), score 0.97";

    private const string SameTitleReason =
        "Fingerprint belongs to a different recording: Sugar and Spice (id-1), score 0.97";

    [Fact]
    public async Task Rejecting_another_song_learns_its_title_for_this_song()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(OtherReason, differentRecording: true, otherTitle: "Please Come Out Tonight");
        var seed = await host.SeedAsync(options => Phil(options, @"@@a\Music\Phil Collins\Both Sides (1993)\11 - Please Come Out Tonight.flac"));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var learned = await host.Context.Blocklist
            .Where(row => row.BlocklistKey == BlocklistKeys.OtherTitle("Please Come Out Tonight"))
            .SingleAsync();
        learned.SongId.Should().Be(seed.SongId);
        learned.SourceType.Should().Be(SourceTypes.Soulseek);
        learned.Reason.Should().Be(OtherReason);
        learned.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_title_learned_from_one_source_applies_to_a_search_of_any_source()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(OtherReason, differentRecording: true, otherTitle: "Please Come Out Tonight");
        var seed = await host.SeedAsync(options => Phil(options, @"@@a\Music\Phil Collins\Both Sides (1993)\11 - Please Come Out Tonight.flac"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var youTubeKeys = await host.Blocklist.GetActiveKeysAsync(SourceTypes.YouTube, seed.SongId, CancellationToken.None);
        youTubeKeys.Should().Contain(BlocklistKeys.OtherTitle("Please Come Out Tonight"))
            .And.NotContain(seed.BlocklistKey, "a Soulseek file's own key means nothing to another source");
    }

    [Fact]
    public async Task A_title_the_song_already_learned_is_not_added_twice()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(OtherReason, differentRecording: true, otherTitle: "Please Come Out Tonight");
        var seed = await host.SeedAsync(options => Phil(options, @"@@a\Music\Phil Collins\Love Songs\09. Please Come out Tonight.flac"));
        host.Context.Blocklist.Add(Row(seed.SongId, BlocklistKeys.OtherTitle("Please Come out Tonight")));
        await host.Context.SaveChangesAsync();

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var keys = await host.Context.Blocklist.Select(row => row.BlocklistKey).ToListAsync();
        BlocklistKeys.OtherTitles(keys).Should().ContainSingle();
    }

    [Fact]
    public async Task Another_recording_with_the_songs_own_title_teaches_no_title()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(SameTitleReason, differentRecording: true, otherTitle: "Sugar and Spice");
        var seed = await host.SeedAsync(options => SaddleClub(options, @"@@a\Music\Nuggets\CD1\18 Sugar and Spice.flac"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var keys = await host.Context.Blocklist.Select(row => row.BlocklistKey).ToListAsync();
        BlocklistKeys.OtherTitles(keys).Should().BeEmpty();
    }

    [Fact]
    public async Task A_verdict_that_is_not_a_different_recording_teaches_nothing()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(OtherReason, differentRecording: false, otherTitle: "Please Come Out Tonight");
        var seed = await host.SeedAsync(options => Phil(options, @"@@a\Various Artists\Hits\11 - Please Come Out Tonight.flac"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        (await host.Context.Blocklist.SingleAsync()).BlocklistKey.Should().Be(seed.BlocklistKey);
    }

    [Fact]
    public async Task A_compilation_file_that_passed_on_its_title_alone_counts_a_miss()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(SameTitleReason, differentRecording: true, otherTitle: "Sugar and Spice");
        var seed = await host.SeedAsync(options => SaddleClub(options, @"@@a\Various Artists\Nuggets\Disc 1\1-18 Sugar And Spice.flac"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var miss = await host.Context.Blocklist
            .Where(row => row.BlocklistKey == BlocklistKeys.CompilationMiss(seed.BlocklistKey))
            .SingleAsync();
        miss.SongId.Should().Be(seed.SongId);
        miss.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task The_same_compilation_miss_is_not_counted_twice()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(SameTitleReason, differentRecording: true, otherTitle: "Sugar and Spice");
        var seed = await host.SeedAsync(options => SaddleClub(options, @"@@a\Various Artists\Nuggets\Disc 1\1-18 Sugar And Spice.flac"));
        host.Context.Blocklist.Add(Row(seed.SongId, BlocklistKeys.CompilationMiss(seed.BlocklistKey)));
        await host.Context.SaveChangesAsync();

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var keys = await host.Context.Blocklist.Select(row => row.BlocklistKey).ToListAsync();
        BlocklistKeys.CompilationMisses(keys).Should().Be(1);
    }

    [Theory]
    [InlineData(@"@@a\Music\The Saddle Club\Singles\05 Sugar and Spice.flac")]
    [InlineData(@"@@a\Various Artists\Kids Hits\03 The Saddle Club - Sugar and Spice.mp3")]
    public async Task A_file_that_named_the_artist_counts_no_miss(string remotePath)
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(SameTitleReason, differentRecording: true, otherTitle: "Sugar and Spice");
        var seed = await host.SeedAsync(options => SaddleClub(options, remotePath));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var keys = await host.Context.Blocklist.Select(row => row.BlocklistKey).ToListAsync();
        BlocklistKeys.CompilationMisses(keys).Should().Be(0);
    }

    [Fact]
    public async Task After_the_second_miss_the_next_search_refuses_such_files_for_this_song_only()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(SameTitleReason, differentRecording: true, otherTitle: "Sugar and Spice");
        var seed = await host.SeedAsync(options => SaddleClub(options, @"@@a\Various Artists\Nuggets\Disc 1\1-18 Sugar And Spice.flac"));
        var otherSongId = await host.SeedOtherSongAsync("0d7f8bd1-6e0e-4c7d-a2a7-5b1c1f5b0d11");
        host.Context.Blocklist.Add(Row(seed.SongId, BlocklistKeys.CompilationMiss(BlocklistKeys.Soulseek("x", @"VA\Hits\05 Sugar and Spice.mp3"))));
        await host.Context.SaveChangesAsync();

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var third = Candidate("userC", @"@@c\Compilations\Sixties Gold\07 Sugar and Spice.mp3");

        var forThisSong = await Judge(host, seed.SongId, third);
        forThisSong.Rejections.Should().ContainSingle().Which.Should().Be(new Rejection(
            RejectionReason.ArtistMismatch,
            "Artist mismatch: Various Artists path without the artist, and 2 such files were other recordings."));

        (await Judge(host, otherSongId, third)).Accepted.Should().BeTrue();
    }

    private static void Phil(ImportSeedOptions options, string remotePath)
    {
        options.Title = "Please Come Tonight";
        options.ArtistName = "Phil";
        options.AlbumArtist = "Phil";
        options.RemotePath = remotePath;
        options.MbArtistId = null;
    }

    private static void SaddleClub(ImportSeedOptions options, string remotePath)
    {
        options.Title = "Sugar and Spice";
        options.ArtistName = "The Saddle Club";
        options.AlbumArtist = "The Saddle Club";
        options.RemotePath = remotePath;
        options.MbArtistId = null;
    }

    private static BlocklistItem Row(long songId, string key) => new()
    {
        SongId = songId,
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = key,
        Reason = "earlier",
    };

    private static VerificationResult Failed(string reason, bool differentRecording, string? otherTitle) =>
        new(
            VerificationOutcome.Failed,
            reason,
            FakeDownloadVerifier.Flac(),
            36,
            "acoustid-1",
            0.97,
            null,
            null,
            false,
            DifferentRecording: differentRecording,
            OtherRecordingTitle: otherTitle);

    /// <summary>Judges a candidate the way a search for the song does: against the song's active blocklist keys.</summary>
    private static async Task<CandidateDecision> Judge(ImportTestHost host, long songId, Candidate candidate)
    {
        var keys = await host.Blocklist.GetActiveKeysAsync(SourceTypes.Soulseek, songId, CancellationToken.None);
        var context = new DecisionContext
        {
            SongTitle = "Sugar and Spice",
            MainArtists = ["The Saddle Club"],
            SongDurationMs = 150_000,
            AlbumTitle = "Singles",
            Profile = SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId),
            Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
            IsBlocklisted = keys.Contains,
            OtherRecordingTitles = BlocklistKeys.OtherTitles(keys),
            CompilationMisses = BlocklistKeys.CompilationMisses(keys),
        };

        return new DecisionEngine().Evaluate(context, [candidate]).Single();
    }

    private static Candidate Candidate(string user, string remotePath) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = BlocklistKeys.Soulseek(user, remotePath),
        DisplayName = remotePath[(remotePath.LastIndexOf('\\') + 1)..],
        RemotePath = remotePath,
        Provider = user,
        QualityId = 36,
        Extension = remotePath[(remotePath.LastIndexOf('.') + 1)..].ToLowerInvariant(),
        DurationMs = 150_000,
        SizeBytes = 8_000_000,
        Availability = new CandidateAvailability(true, 0, 2_000_000),
        Parsed = SoulseekFilenameParser.Parse(remotePath).Parsed,
    };
}
