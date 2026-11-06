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
/// A Soulseek file refused because its fingerprint belongs to a different recording is remembered by
/// its folder and file name, so the same file from another user is skipped (review 2026-11-04, new-06).
/// </summary>
public sealed class ImportServiceDifferentRecordingTests
{
    private const string Title = "Sugar and Spice";
    private const string RemotePath = @"@@a\Music\Nuggets\CD1\18 Sugar and Spice.flac";
    private const string DifferentRecording =
        "Fingerprint belongs to a different recording: Sugar and Spice (id-1), score 0.97";

    [Fact]
    public async Task Rejecting_a_different_recording_blocklists_the_file_by_folder_and_name_too()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(DifferentRecording, differentRecording: true);
        var seed = await host.SeedAsync(options => Seed(options, RemotePath));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var rows = await host.Context.Blocklist.OrderBy(row => row.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.BlocklistKey).Should().Equal(
            seed.BlocklistKey,
            BlocklistKeys.SoulseekFile(RemotePath, Title));
        rows.Should().OnlyContain(row =>
            row.SongId == seed.SongId
            && row.SourceType == SourceTypes.Soulseek
            && row.Reason == DifferentRecording
            && row.ExpiresAt == null);
    }

    [Fact]
    public async Task A_later_search_skips_the_same_file_from_another_user_for_this_song_only()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(DifferentRecording, differentRecording: true);
        var seed = await host.SeedAsync(options => Seed(options, RemotePath));
        var otherSongId = await host.SeedOtherSongAsync("0d7f8bd1-6e0e-4c7d-a2a7-5b1c1f5b0d11");

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var sameFileOtherUser = Candidate("userB", @"@@b\Deeper\Share\Music\Nuggets\cd1\18 SUGAR AND SPICE.mp3");

        var forThisSong = await Judge(host, seed.SongId, sameFileOtherUser);
        forThisSong.Rejections.Should().Contain(new Rejection(
            RejectionReason.Blocklisted,
            "Blocklisted: the same file from another user was a different recording."));

        var forAnotherSong = await Judge(host, otherSongId, sameFileOtherUser);
        forAnotherSong.Rejections.Should().NotContain(rejection => rejection.Reason == RejectionReason.Blocklisted);
    }

    [Fact]
    public async Task Rejecting_for_any_other_reason_blocklists_the_one_copy_only()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed("The file is 12 s long; the song is 150 s", differentRecording: false);
        var seed = await host.SeedAsync(options => Seed(options, RemotePath));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        (await host.Context.Blocklist.SingleAsync()).BlocklistKey.Should().Be(seed.BlocklistKey);
    }

    [Fact]
    public async Task A_file_named_just_the_title_teaches_nothing()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(DifferentRecording, differentRecording: true);
        var seed = await host.SeedAsync(options => Seed(options, @"@@a\Music\Sugar and Spice.mp3"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        (await host.Context.Blocklist.SingleAsync()).BlocklistKey.Should().Be(seed.BlocklistKey);
    }

    [Fact]
    public async Task A_file_in_a_disc_folder_is_keyed_by_the_album_folder_above_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(DifferentRecording, differentRecording: true);
        var seed = await host.SeedAsync(
            options => Seed(options, @"@@a\Music\Nuggets Original Artyfacts\CD1\18 Sugar and Spice.flac"));

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        var learned = await host.Context.Blocklist.Where(row => row.BlocklistKey != seed.BlocklistKey).SingleAsync();
        learned.BlocklistKey.Should().Be(string.Join(
            BlocklistKeys.Separator,
            "soulseek-file",
            "nuggets original artyfacts/cd1",
            "18 sugar and spice"));
    }

    [Fact]
    public async Task A_file_the_blocklist_already_holds_by_folder_and_name_is_not_added_twice()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed(DifferentRecording, differentRecording: true);
        var seed = await host.SeedAsync(options => Seed(options, RemotePath));
        host.Context.Blocklist.Add(new BlocklistItem
        {
            SongId = seed.SongId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.SoulseekFile(@"@@x\Nuggets\cd1\18 sugar and spice.mp3")!,
            Reason = "earlier",
        });
        await host.Context.SaveChangesAsync();

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        (await host.Context.Blocklist.CountAsync()).Should().Be(2);
    }

    private static void Seed(ImportSeedOptions options, string remotePath)
    {
        options.Title = Title;
        options.ArtistName = "The Saddle Club";
        options.AlbumArtist = "The Saddle Club";
        options.RemotePath = remotePath;
        options.MbArtistId = null;
    }

    private static VerificationResult Failed(string reason, bool differentRecording) =>
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
            DifferentRecording: differentRecording);

    /// <summary>Judges a candidate the way a search for the song does: against the song's active blocklist keys.</summary>
    private static async Task<CandidateDecision> Judge(ImportTestHost host, long songId, Candidate candidate)
    {
        var keys = await host.Blocklist.GetActiveKeysAsync(SourceTypes.Soulseek, songId, CancellationToken.None);
        var context = new DecisionContext
        {
            SongTitle = Title,
            MainArtists = ["The Saddle Club"],
            SongDurationMs = 150_000,
            AlbumTitle = "Singles",
            Profile = SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId),
            Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
            IsBlocklisted = keys.Contains,
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
