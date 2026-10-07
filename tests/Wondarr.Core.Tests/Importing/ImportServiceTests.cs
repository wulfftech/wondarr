using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The import pipeline end to end: verify, check the quality, tag, name, place, record — and, when a
/// file is refused, blocklist it and ask the search for the next candidate (ARCHITECTURE §5.2).
/// </summary>
public sealed class ImportServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Imports_a_verified_download_into_the_library()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x01, 0x02 };
        host.Covers.Bytes = cover;

        var seed = await host.SeedAsync(options =>
        {
            options.CoverUrl = "https://coverartarchive.test/front.jpg";
            options.MbReleaseId = "f2e4a1c0-1111-4222-8333-444455556666";
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var request = host.Placer.Requests.Should().ContainSingle().Subject;
        request.RelativePathWithoutExtension.Should().Be(ImportTestHost.DaftPunkRelativePath);
        request.Extension.Should().Be("flac");
        request.Mode.Should().Be(TransferMode.Move);
        request.LibraryRoot.Should().Be("/data/music");
        request.ReplacesPath.Should().BeNull();
        request.SourcePath.Should().Be(seed.DownloadPath);

        host.Covers.Urls.Should().Equal("https://coverartarchive.test/front.jpg");
        host.TagWriter.Writes.Should().ContainSingle();
        host.TagWriter.Writes[0].Path.Should().Be(seed.DownloadPath);
        host.TagWriter.Writes[0].Tags.Album.Should().Be("Random Access Memories");
        host.TagWriter.Writes[0].Tags.Artist.Should().Be("Daft Punk");
        host.TagWriter.Writes[0].Tags.FrontCover.Should().BeSameAs(cover);

        var file = await host.Context.SongFiles.SingleAsync();
        file.SongId.Should().Be(seed.SongId);
        file.Path.Should().Be("/data/music/" + ImportTestHost.DaftPunkRelativePath + ".flac");
        file.Size.Should().Be(30_000_000);
        file.Codec.Should().Be("flac");
        file.Container.Should().Be("flac");
        file.SampleRate.Should().Be(44_100);
        file.BitDepth.Should().Be(16);
        file.Channels.Should().Be(2);
        file.QualityId.Should().Be(36);
        file.AcoustId.Should().Be("acoustid-1");
        file.FingerprintVerified.Should().BeTrue();
        file.SourceType.Should().Be(SourceTypes.Soulseek);
        file.ImportedAt.Should().Be(Start.UtcDateTime);
        file.TagsWritten.Should().NotBeNullOrEmpty();

        using var sourceRef = JsonDocument.Parse(file.SourceRef!);
        sourceRef.RootElement.GetProperty("provider").GetString().Should().Be("peer");
        sourceRef.RootElement.GetProperty("remotePath").GetString().Should().Be(seed.RemotePath);
        sourceRef.RootElement.GetProperty("candidateId").GetInt64().Should().Be(seed.CandidateId);
        sourceRef.RootElement.GetProperty("queueItemId").GetInt64().Should().Be(seed.QueueItemId);
        sourceRef.RootElement.GetProperty("searchRunId").GetInt64().Should().Be(seed.SearchRunId);

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Imported);
        history.QualityId.Should().Be(36);

        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("path").GetString().Should().Be(file.Path);

        // Nulls are left out of the payload: a first import replaced nothing.
        data.RootElement.TryGetProperty("previousPath", out _).Should().BeFalse();
        data.RootElement.TryGetProperty("recycledPath", out _).Should().BeFalse();
        data.RootElement.GetProperty("acoustId").GetString().Should().Be("acoustid-1");

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Imported);
        item.Progress.Should().Be(1);
        item.FinishedAt.Should().Be(Start.UtcDateTime);
        item.Message.Should().BeNull();

        var peer = await host.Context.SoulseekUsers.SingleAsync(user => user.Username == "peer");
        peer.Successes.Should().Be(1);
        peer.Failures.Should().Be(0);

        host.Events.Of<QueueItemChangedEvent>()[^1].State.Should().Be(QueueItemState.Imported);
        var imported = host.Events.Of<SongImportedEvent>().Should().ContainSingle().Subject;
        imported.SongId.Should().Be(seed.SongId);
        imported.SongFileId.Should().Be(file.Id);
        imported.Upgraded.Should().BeFalse();

        host.Search.Grabs.Should().BeEmpty();
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Upgrades_the_file_the_song_already_held()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Upgraded);

        host.Placer.Requests.Should().ContainSingle().Subject.ReplacesPath.Should().Be(held);

        var file = await host.Context.SongFiles.SingleAsync();
        file.Id.Should().Be(seed.SongFileId);
        file.Path.Should().Be("/data/music/" + ImportTestHost.DaftPunkRelativePath + ".flac");
        file.QualityId.Should().Be(36);
        file.Codec.Should().Be("flac");

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Upgraded);
        history.QualityId.Should().Be(36);

        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("previousPath").GetString().Should().Be(held);

        host.Events.Of<SongImportedEvent>().Should().ContainSingle().Subject.Upgraded.Should().BeTrue();
    }

    [Fact]
    public async Task An_automatic_upgrade_the_fingerprint_cannot_confirm_is_rejected()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        // AcoustID does not know the file: it passes on probe and length only.
        host.Verifier.Result = FakeDownloadVerifier.Passed() with
        {
            Reason = "Not in AcoustID; verified by probe and duration only",
            AcoustId = null,
            FingerprintScore = null,
            FingerprintVerified = false,
        };

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        host.Placer.Requests.Should().BeEmpty("the held file is never touched");
        (await host.Context.SongFiles.SingleAsync()).Path.Should().Be(held);
        (await host.Context.QueueItems.SingleAsync(item => item.Id == seed.QueueItemId)).Message
            .Should().Contain("could not be confirmed by its fingerprint");
    }

    [Fact]
    public async Task A_manual_grab_may_replace_a_file_with_one_the_fingerprint_cannot_confirm()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
            options.CurrentFileQualityId = 29;
            options.Trigger = SearchTrigger.Manual;
        });
        host.Verifier.Result = FakeDownloadVerifier.Passed() with { AcoustId = null, FingerprintScore = null, FingerprintVerified = false };

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);
    }

    [Fact]
    public async Task Replaces_the_file_a_manual_grab_brought_even_when_it_is_not_an_upgrade()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 36;
            options.Trigger = SearchTrigger.Manual;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Upgraded);
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_grab_for_a_song_owned_through_a_reference_file_never_recycles_the_users_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var referenced = "/reference/music/Daft Punk/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = referenced;
            options.CurrentFileQualityId = 29;
            options.CurrentFileSourceType = SourceTypes.Reference;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        // The file row is repointed at the imported file as for any upgrade, but Wondarr does not own
        // the reference file, so nothing is asked to replace or recycle it.
        outcome.Should().Be(ImportOutcome.Upgraded);
        host.Placer.Requests.Should().ContainSingle().Subject.ReplacesPath.Should().BeNull();

        var file = await host.Context.SongFiles.SingleAsync();
        file.Id.Should().Be(seed.SongFileId);
        file.SourceType.Should().Be(SourceTypes.Soulseek);
        file.Path.Should().Be("/data/music/" + ImportTestHost.DaftPunkRelativePath + ".flac");
    }

    [Fact]
    public async Task Rejects_a_grab_that_is_not_an_upgrade()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 36;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.Message.Should().StartWith("Not an upgrade:");

        (await host.Blocklist
                .IsBlocklistedAsync(SourceTypes.Soulseek, seed.BlocklistKey, CancellationToken.None))
            .Should().BeTrue();

        (await host.Context.SongFiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Rejects_a_verification_failure_and_grabs_the_next_candidate()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Failed,
            "Fingerprint matches a different recording",
            FakeDownloadVerifier.Flac(),
            36,
            null,
            null,
            "someone-else",
            null,
            false);
        host.Search.Next = 77;

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var blocked = await host.Context.Blocklist.SingleAsync();
        blocked.SongId.Should().Be(seed.SongId);
        blocked.SourceType.Should().Be(SourceTypes.Soulseek);
        blocked.BlocklistKey.Should().Be(seed.BlocklistKey);
        blocked.Reason.Should().Be("Fingerprint matches a different recording");
        blocked.ExpiresAt.Should().BeNull();

        var peer = await host.Context.SoulseekUsers.SingleAsync(user => user.Username == "peer");
        peer.Failures.Should().Be(1);
        peer.Successes.Should().Be(0);

        File.Exists(seed.DownloadPath).Should().BeFalse();
        Directory.Exists(seed.DownloadDirectory).Should().BeFalse();

        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.FinishedAt.Should().Be(Start.UtcDateTime);

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Rejected);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("reason").GetString()
            .Should().Be("Fingerprint matches a different recording");
        data.RootElement.GetProperty("nextQueueItemId").GetString().Should().Be("77");
    }

    [Fact]
    public async Task Stops_asking_for_candidates_once_the_attempt_budget_is_spent()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Failed,
            "The file does not decode",
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        host.Search.Next = 77;

        var seed = await host.SeedAsync(options =>
            options.Attempt = host.Options.MaxAutoAttemptsPerSearch);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        host.Search.Grabs.Should().BeEmpty();

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Rejected);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("nextQueueItemId").GetString().Should().Be("no more candidates");
    }

    [Fact]
    public async Task Recovers_when_the_next_candidate_cannot_be_grabbed()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Failed,
            "The file does not decode",
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        host.Search.GrabFailure = new InvalidOperationException("slskd is unreachable");

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Rejected);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("nextQueueItemId").GetString().Should().Be("no more candidates");
    }

    [Fact]
    public async Task Rejects_a_file_whose_measured_quality_the_profile_forbids()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = FakeDownloadVerifier.Passed(
            2,
            new MediaInfo("mp3", "mp3", 8, 22_050, null, 1, 369_000, false, 1_000_000));

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.Message.Should().Be("Measured quality MP3-8 is not allowed by Standard 320");

        (await host.Blocklist
                .IsBlocklistedAsync(SourceTypes.Soulseek, seed.BlocklistKey, CancellationToken.None))
            .Should().BeTrue();

        (await host.Context.SongFiles.CountAsync()).Should().Be(0);
        host.TagWriter.Writes.Should().BeEmpty();
        host.Placer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fails_without_blocklisting_or_moving_on_when_the_file_could_not_be_tagged()
    {
        // A verified file that cannot be tagged is the tag writer's problem: blocklisting it (and every
        // next candidate, which would fail the same way) burned good files on the first live run.
        await using var host = await ImportTestHost.CreateAsync();
        host.TagWriter.Success = false;
        host.TagWriter.Error = "The file is not a writable container";

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be("Tagging failed: The file is not a writable container");
        host.Placer.Requests.Should().BeEmpty();
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().BeEmpty();
    }

    // The transcode-step tests (YouTube item, KeepOpus, Soulseek never, failed transcode) live in
    // ImportServiceTranscodeTests, the file the P4-03 deliverable names.

    [Fact]
    public async Task Defers_the_import_when_the_verdict_could_not_be_reached()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Deferred,
            "AcoustID is unavailable",
            null,
            null,
            null,
            null,
            null,
            null,
            false);

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Completed);
        item.Message.Should().Be("AcoustID is unavailable");
        item.NextCheckAt.Should().Be((Start + TimeSpan.FromMinutes(15)).UtcDateTime);
        item.FinishedAt.Should().BeNull();

        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Placer.Requests.Should().BeEmpty();
        host.Search.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task Imports_a_file_that_only_needs_review_without_a_verified_fingerprint()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.NeedsReview,
            "No fingerprint was taken; the duration matched",
            FakeDownloadVerifier.Flac(),
            36,
            "acoustid-2",
            0.41,
            null,
            null,
            false);

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var file = await host.Context.SongFiles.SingleAsync();
        file.FingerprintVerified.Should().BeFalse();
        file.AcoustId.Should().Be("acoustid-2");

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Imported);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("needsReview").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Adopts_the_recording_id_acoustid_recognised()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = FakeDownloadVerifier.Passed() with
        {
            LearnedMbRecordingId = "df6e2f3a-9c44-4a1c-9c1f-1b2c3d4e5f60",
        };

        var seed = await host.SeedAsync(options => options.MbRecordingId = null);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var song = await host.Context.Songs.SingleAsync(entry => entry.Id == seed.SongId);
        song.MbRecordingId.Should().Be("df6e2f3a-9c44-4a1c-9c1f-1b2c3d4e5f60");

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Imported);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("learnedMbid").GetString()
            .Should().Be("df6e2f3a-9c44-4a1c-9c1f-1b2c3d4e5f60");
    }

    [Fact]
    public async Task Does_not_adopt_a_recording_id_another_song_already_holds()
    {
        await using var host = await ImportTestHost.CreateAsync();

        const string Learned = "df6e2f3a-9c44-4a1c-9c1f-1b2c3d4e5f60";
        await host.SeedOtherSongAsync(Learned);

        host.Verifier.Result = FakeDownloadVerifier.Passed() with { LearnedMbRecordingId = Learned };

        var seed = await host.SeedAsync(options => options.MbRecordingId = null);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var song = await host.Context.Songs.SingleAsync(entry => entry.Id == seed.SongId);
        song.MbRecordingId.Should().BeNull();

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Imported);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.TryGetProperty("learnedMbid", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Fails_without_blocklisting_when_the_file_cannot_be_placed()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Placer.Success = false;
        host.Placer.Error = "The library root is not writable";

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be("The library root is not writable");

        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().BeEmpty();
        (await host.Context.SongFiles.CountAsync()).Should().Be(0);

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Failed);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("error").GetString().Should().Be("The library root is not writable");
    }

    [Fact]
    public async Task Rejects_a_webm_before_it_is_tagged_or_placed()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Search.Next = 91;

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky.webm");

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be("Refusing to import .webm files");

        // The file is refused before anything is written to it, or asked about it.
        host.Verifier.Requests.Should().BeEmpty();
        host.Covers.Urls.Should().BeEmpty();
        host.TagWriter.Writes.Should().BeEmpty();
        host.Placer.Requests.Should().BeEmpty();

        (await host.Blocklist
                .IsBlocklistedAsync(SourceTypes.Soulseek, seed.BlocklistKey, CancellationToken.None))
            .Should().BeTrue();
        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));
        (await host.Context.SongFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Rejects_a_download_with_no_audio_file_extension()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Search.Next = 92;

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky");

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.Message.Should().Be("The download has no audio file extension");

        host.TagWriter.Writes.Should().BeEmpty();
        host.Placer.Requests.Should().BeEmpty();

        (await host.Blocklist
                .IsBlocklistedAsync(SourceTypes.Soulseek, seed.BlocklistKey, CancellationToken.None))
            .Should().BeTrue();
        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));
    }

    [Theory]
    [InlineData("dotdot")]
    [InlineData("outside")]
    [InlineData("root")]
    [InlineData("lookalike")]
    public async Task Refuses_to_delete_a_download_that_is_not_the_items_own_folder(string kind)
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = NotOurRecording();

        // The item's own folder is <downloads>/wondarr/<guid>; everything below is somewhere else.
        var seed = await host.SeedAsync(options => options.CreateDownload = false);

        var wondarr = Path.GetDirectoryName(seed.DownloadDirectory)!;
        var downloads = Path.GetDirectoryName(wondarr)!;
        var stem = Guid.NewGuid().ToString("N");

        var (file, downloadPath) = kind switch
        {
            "dotdot" => (
                Path.Combine(wondarr, "escaped.flac"),
                Path.Combine(seed.DownloadDirectory, "..", "escaped.flac")),
            "outside" => (
                Path.Combine(downloads, "elsewhere", "escaped.flac"),
                Path.Combine(downloads, "elsewhere", "escaped.flac")),
            "root" => (
                Path.Combine(downloads, "loose.flac"),
                Path.Combine(downloads, "loose.flac")),
            _ => (
                Path.Combine(wondarr, stem, "lookalike.flac"),
                Path.Combine(wondarr, stem, "lookalike.flac")),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, new byte[64]);
        await host.SetDownloadPathAsync(seed.QueueItemId, downloadPath);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        File.Exists(file).Should().BeTrue("only the item's own download folder is Wondarr's to empty");
        Directory.Exists(seed.DownloadDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_to_delete_through_a_symlinked_download_folder()
    {
        if (!!OperatingSystem.IsWindows())
        {
            // Creating a symlink on Windows needs privileges the test host cannot assume.
            return;
        }

        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = NotOurRecording();

        var seed = await host.SeedAsync(options => options.CreateDownload = false);

        var real = seed.DownloadDirectory + "-real";
        Directory.CreateDirectory(real);

        var file = Path.Combine(real, "08 - Get Lucky.flac");
        await File.WriteAllBytesAsync(file, new byte[64]);

        // The item's own folder exists, but it is a symlink: deleting through it would reach a file
        // that is not in any folder the item owns.
        Directory.Delete(seed.DownloadDirectory);
        Directory.CreateSymbolicLink(seed.DownloadDirectory, real);

        await host.SetDownloadPathAsync(
            seed.QueueItemId,
            Path.Combine(seed.DownloadDirectory, "08 - Get Lucky.flac"));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);
        File.Exists(file).Should().BeTrue("nothing is deleted through a reparse point");
    }

    [Fact]
    public async Task Fails_without_throwing_when_the_record_cannot_be_saved()
    {
        await using var host = await ImportTestHost.CreateAsync();

        ImportSeed? seed = null;

        // A song_file row appears for the song behind the import's back, so its own insert collides
        // with the one-file-per-song index when the record step saves.
        host.Placer.OnPlaceAsync = _ => host.InsertSongFileBehindAsync(seed!.SongId, "/data/music/other.flac");

        seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().StartWith("Imported to /data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac");
        item.Message.Should().Contain("but recording it failed:");

        // The unsuccessful import never announced itself as one.
        host.Events.Of<SongImportedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_learn_a_recording_id_from_a_file_it_refuses()
    {
        await using var host = await ImportTestHost.CreateAsync();

        const string Learned = "df6e2f3a-9c44-4a1c-9c1f-1b2c3d4e5f60";

        // A file that taught us the recording id, but whose measured quality the profile forbids.
        host.Verifier.Result = FakeDownloadVerifier
            .Passed(2, new MediaInfo("mp3", "mp3", 8, 22_050, null, 1, 369_000, false, 1_000_000))
            with
            {
                LearnedMbRecordingId = Learned,
            };

        var seed = await host.SeedAsync(options => options.MbRecordingId = null);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var song = await host.Context.Songs.SingleAsync(entry => entry.Id == seed.SongId);
        song.MbRecordingId.Should().BeNull("a refused file does not rename the song");

        (await host.Context.History
                .CountAsync(entry => entry.SongId == seed.SongId && entry.EventType == HistoryEventType.Imported))
            .Should().Be(0);
    }

    [Fact]
    public async Task Fails_and_tries_the_next_candidate_when_the_download_is_missing()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Search.Next = 88;

        var seed = await host.SeedAsync(options => options.CreateDownload = false);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.Message.Should().Be($"Downloaded file is missing: {seed.DownloadPath}");

        host.Verifier.Requests.Should().BeEmpty();
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));

        var history = await LastEventAsync(host, seed.SongId, HistoryEventType.Failed);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("nextQueueItemId").GetString().Should().Be("88");
    }

    [Fact]
    public async Task Is_not_ready_until_the_source_reports_the_download_complete()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var seed = await host.SeedAsync(options => options.State = QueueItemState.Downloading);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.NotReady);

        host.Verifier.Requests.Should().BeEmpty();
        host.Placer.Requests.Should().BeEmpty();
        host.Events.Published.Should().BeEmpty();

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Downloading);
    }

    [Fact]
    public async Task Is_not_ready_when_the_queue_item_does_not_exist()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var outcome = await host.Import.ImportAsync(4242, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.NotReady);
        host.Events.Published.Should().BeEmpty();
    }

    /// <summary>A verdict that says the file is not the recording the song asked for.</summary>
    private static VerificationResult NotOurRecording(string reason = "Fingerprint matches a different recording") =>
        new(VerificationOutcome.Failed, reason, FakeDownloadVerifier.Flac(), 36, null, null, "someone-else", null, false);

    private static Task<HistoryItem> LastEventAsync(
        ImportTestHost host,
        long songId,
        HistoryEventType eventType) =>
        host.Context.History
            .Where(item => item.SongId == songId && item.EventType == eventType)
            .OrderByDescending(item => item.Id)
            .FirstAsync();
}
