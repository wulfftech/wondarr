using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// "Once a file has moved, the database records where it is" (code review 2026-10-10, M-12): the
/// import's behaviour around a placement that moved the file and failed, a shutdown between the
/// placement and the record, a YouTube transcode that is later deferred or rejected, the sidecars of
/// an upgrade, a container the tag writer cannot handle, and a rejected AcoustID key.
/// </summary>
public class ImportServiceFileRecordTests
{
    private static readonly VerificationResult Failed =
        new(VerificationOutcome.Failed, "Fingerprint matches a different recording", FakeDownloadVerifier.Flac(), 36, null, null, "someone-else", null, false);

    private static VerificationResult Deferred(bool keyRejected = false) =>
        new(VerificationOutcome.Deferred, "AcoustID is unavailable", null, null, null, null, null, null, false, keyRejected);

    [Fact]
    public async Task Records_a_file_the_placer_reports_as_failed_but_that_did_land()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Placer.LandedWithError = "chmod failed: operation not permitted";

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var target = host.LibraryRoot + "/" + ImportTestHost.DaftPunkRelativePath + ".flac";
        File.Exists(target).Should().BeTrue();

        (await host.Context.SongFiles.SingleAsync()).Path.Should().Be(target, "the row says where the file is");
        (await host.Context.QueueItems.SingleAsync()).State.Should().Be(QueueItemState.Imported);
        host.Search.Grabs.Should().BeEmpty("no next candidate is grabbed for a file that is already placed");
    }

    [Fact]
    public async Task Records_the_file_when_the_import_is_cancelled_after_the_placement()
    {
        await using var host = await ImportTestHost.CreateAsync();
        using var shutdown = new CancellationTokenSource();

        // The container stops while the file is being moved into the library.
        host.Placer.OnPlaceAsync = _ =>
        {
            shutdown.Cancel();

            return Task.CompletedTask;
        };

        var seed = await host.SeedAsync();

        try
        {
            await host.Import.ImportAsync(seed.QueueItemId, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // Telling the world about the import may notice the shutdown; the record may not.
        }

        var file = await host.Context.SongFiles.SingleAsync();
        file.Path.Should().Be(host.LibraryRoot + "/" + ImportTestHost.DaftPunkRelativePath + ".flac");
        (await host.Context.QueueItems.AsNoTracking().SingleAsync()).State.Should().Be(QueueItemState.Imported);
    }

    [Fact]
    public async Task A_deferred_youtube_import_comes_back_to_the_transcoded_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Deferred();

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var transcoded = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Deferred);

        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.State.Should().Be(QueueItemState.Completed);
        item.DownloadPath.Should().Be(transcoded, "the Opus original is gone, and the item must say so");
        File.Exists(transcoded).Should().BeTrue();

        // The next pass finds the file, transcodes nothing again, and imports it.
        host.Verifier.Result = FakeDownloadVerifier.Passed();

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        host.Transcoder.Requests.Should().ContainSingle("the second pass skips the transcode");
        host.Placer.Requests.Should().ContainSingle().Which.SourcePath.Should().Be(transcoded);
    }

    [Fact]
    public async Task A_rejected_youtube_import_deletes_the_transcoded_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed;

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var transcoded = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        File.Exists(transcoded).Should().BeFalse("the rejection cleans up the file that is really there");
        File.Exists(seed.DownloadPath).Should().BeFalse();
        Directory.Exists(seed.DownloadDirectory).Should().BeFalse("the per-grab folder goes with it");
    }

    [Fact]
    public async Task A_failed_tagging_does_not_save_the_organizers_album_alignment()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.TagWriter.Success = false;
        host.TagWriter.Error = "The file is not a writable container";

        // A file of the same album is already in the library, under another album title: the
        // organizer re-files the incoming song under that title before it tags.
        var first = await host.SeedAsync(options =>
        {
            options.AlbumKey = "shared";
            options.AlbumTitle = "Random Access Memories (Deluxe)";
            options.CurrentFilePath = host.LibraryRoot + "/Daft Punk/Random Access Memories/01 - Give Life Back to Music.flac";
            options.Title = "Give Life Back to Music";
        });

        var second = await host.SeedAsync(options =>
        {
            options.AlbumKey = "shared";
            options.AlbumTitle = "Random Access Memories";
            options.MbArtistId = null;
        });

        (await host.Import.ImportAsync(second.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        first.SongId.Should().NotBe(second.SongId);

        var album = await host.Context.AlbumContexts.AsNoTracking().SingleAsync(entry => entry.SongId == second.SongId);
        album.AlbumTitle.Should().Be("Random Access Memories", "a failed import re-files nothing");
    }

    [Fact]
    public async Task Moves_the_old_files_sidecars_when_an_upgrade_renames_the_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories";
        Directory.CreateDirectory(folder);

        var held = folder + "/08 - Get Lucky (old).mp3";
        await File.WriteAllBytesAsync(held, new byte[8]);
        await File.WriteAllTextAsync(folder + "/08 - Get Lucky (old).lrc", "[00:01.00]hello");

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);

        File.Exists(folder + "/08 - Get Lucky.lrc").Should().BeTrue("the lyrics follow the upgraded file");
        File.Exists(folder + "/08 - Get Lucky (old).lrc").Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_a_container_the_tag_writer_cannot_handle()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Probe.Result = new MediaProbeResult(true, Ape(), null);

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky.ape");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync();
        item.Message.Should().Contain("Cannot tag .ape files");
        host.TagWriter.Writes.Should().BeEmpty();
        host.Verifier.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Imports_an_ape_when_the_lossless_rule_converts_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"flac"}}""");
        host.Probe.Result = new MediaProbeResult(true, Ape(), null);

        // The seeded profiles do not allow APE; a custom profile that does is what this is about.
        await using (var context = host.Database.CreateContext(host.Time))
        {
            var profile = await context.QualityProfiles.SingleAsync(entry => entry.Id == SeedData.LosslessProfileId);
            profile.Items = [.. profile.Items.Select(item => item.QualityIds.Contains(38) ? item with { Allowed = true } : item)];
            await context.SaveChangesAsync();
        }

        var seed = await host.SeedAsync(options =>
        {
            options.DownloadName = "08 - Get Lucky.ape";
            options.QualityProfileId = SeedData.LosslessProfileId;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported, (await host.Context.QueueItems.AsNoTracking().SingleAsync()).Message);
        host.Transcoder.Requests.Should().ContainSingle();
        host.Placer.Requests.Should().ContainSingle().Which.Extension.Should().Be("flac");
    }

    [Fact]
    public async Task A_rejected_acoustid_key_defers_a_bounded_number_of_times_and_then_fails_without_blocklisting()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Deferred(keyRejected: true);

        var seed = await host.SeedAsync();

        for (var attempt = 1; attempt <= AcoustIdKeyState.MaxKeyDeferrals; attempt++)
        {
            (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None))
                .Should().Be(ImportOutcome.Deferred, $"deferral {attempt} is within the bound");
        }

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Contain("AcoustID").And.Contain("client key").And.Contain("Metadata services");

        (await host.Context.Blocklist.CountAsync()).Should().Be(0, "the file was not at fault");
        host.Search.Grabs.Should().BeEmpty();
    }

    private static MediaInfo Ape() => new("ape", "ape", 900, 44_100, 16, 2, 369_000, true, 30_000_000);
}
