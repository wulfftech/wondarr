using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The conversion step of the import pipeline (P6-08): every download is converted per the
/// library's output policy — one rule per source class — between the extension check and
/// verification, the file stays ranked as the quality that was downloaded, and a converted
/// original is deleted only after the converted file is placed and recorded.
/// </summary>
public class ImportServiceTranscodeTests
{
    [Fact]
    public async Task YouTube_item_transcodes_before_verification()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        // The default policy: AAC 256 kbps CBR in an .m4a, written next to the Opus original under
        // the video id the candidate's remote path holds.
        var request = host.Transcoder.Requests.Should().ContainSingle().Subject;
        request.SourcePath.Should().Be(seed.DownloadPath);
        request.Policy.Codec.Should().Be(OutputCodec.Aac);
        request.Policy.Mode.Should().Be(OutputMode.Cbr);
        request.Policy.BitrateKbps.Should().Be(256);
        request.SourceIsLossless.Should().BeFalse();
        request.DestinationPath.Should().Be(
            Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a"));

        // The Opus original is deleted once the transcode has the audio.
        File.Exists(seed.DownloadPath).Should().BeFalse();

        // The file that is verified, tagged and placed is the transcode, ranked as the Opus stream
        // the download was (ADR-0008) — the quality id is pinned to the source, while the measured
        // bitrate is still what the probe measured on the transcoded file.
        var transcoded = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a");
        host.Verifier.Requests.Should().ContainSingle().Which.Path.Should().Be(transcoded);
        host.Verifier.Requests[0].SourceQualityId.Should().Be(SeedData.Opus160QualityId);

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(transcoded);
        placement.Extension.Should().Be("m4a");

        var file = await host.Context.SongFiles.SingleAsync();
        file.QualityId.Should().Be(SeedData.Opus160QualityId);
        file.SourceType.Should().Be(SourceTypes.YouTube);
        file.BitrateKbps.Should().Be(1000, "the probe's measured bitrate is still recorded");
        file.Path.Should().Be("/data/music/" + ImportTestHost.DaftPunkRelativePath + ".m4a");
    }

    [Fact]
    public async Task Keep_imports_the_remux()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"codec":"keepOpus"}""");

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Transcoder.Requests.Should().BeEmpty();
        File.Exists(seed.DownloadPath).Should().BeTrue();

        host.Verifier.Requests.Should().ContainSingle().Which.Path.Should().Be(seed.DownloadPath);
        host.Verifier.Requests[0].SourceQualityId.Should().Be(SeedData.Opus160QualityId);

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(seed.DownloadPath);
        placement.Extension.Should().Be("opus");

        (await host.Context.SongFiles.SingleAsync()).QualityId.Should().Be(SeedData.Opus160QualityId);
    }

    [Fact]
    public async Task Keep_with_the_ogg_container_renames_the_remux()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"youtube":{"codec":"keep","opusContainer":"ogg"}}""");

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        // An .opus file is already an Ogg container, so the ogg container is a rename, never a
        // re-encode: no transcode runs, and the same bytes land under .ogg.
        host.Transcoder.Requests.Should().BeEmpty();

        var renamed = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.ogg");
        File.Exists(renamed).Should().BeTrue();
        File.Exists(seed.DownloadPath).Should().BeFalse();

        host.Verifier.Requests.Should().ContainSingle().Which.Path.Should().Be(renamed);

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(renamed);
        placement.Extension.Should().Be("ogg");

        (await host.Context.SongFiles.SingleAsync()).Path.Should().EndWith(".ogg");
    }

    [Fact]
    public async Task Soulseek_item_never_transcodes()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Transcoder.Requests.Should().BeEmpty();
        host.Verifier.Requests.Should().ContainSingle().Which.SourceQualityId.Should().BeNull();
    }

    [Fact]
    public async Task A_lossless_Soulseek_file_is_converted_per_the_lossless_rule()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"mp3","mode":"cbr","bitrateKbps":320}}""");

        var seed = await host.SeedAsync();

        // The verifier probes the converted file, so it answers with an MP3.
        host.Verifier.Result = FakeDownloadVerifier.Passed(
            media: new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 369_000, false, 5_000_000));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        // The rule the probe picked: the file is lossless, so the lossless rule converts it to
        // MP3 320, into a sibling temporary file.
        var request = host.Transcoder.Requests.Should().ContainSingle().Subject;
        request.SourcePath.Should().Be(seed.DownloadPath);
        request.SourceIsLossless.Should().BeTrue();
        request.Policy.Codec.Should().Be(OutputCodec.Mp3);
        request.Policy.BitrateKbps.Should().Be(320);
        request.DestinationPath.Should().Be(
            Path.Combine(seed.DownloadDirectory, "08 - Get Lucky.wondarr-convert.mp3"));

        // The file that is verified, tagged and placed is the conversion, ranked as the FLAC that
        // was downloaded (#5) — while the codec and bitrate on disk are the conversion's.
        host.Verifier.Requests.Should().ContainSingle()
            .Which.SourceQualityId.Should().Be(36, "the FLAC quality the probe measured");
        host.Verifier.Requests[0].Path.Should().Be(request.DestinationPath);

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(request.DestinationPath);
        placement.Extension.Should().Be("mp3");

        var file = await host.Context.SongFiles.SingleAsync();
        file.QualityId.Should().Be(36, "the FLAC quality the probe measured");
        file.Codec.Should().Be("mp3");
        file.Container.Should().Be("mp3");
        file.BitrateKbps.Should().Be(320);
        file.Path.Should().EndWith(".mp3");

        // The downloaded original is deleted only now, and the temporary is never left behind.
        File.Exists(seed.DownloadPath).Should().BeFalse();
        File.Exists(request.DestinationPath).Should().BeFalse();
    }

    [Fact]
    public async Task A_lossy_Soulseek_file_already_in_the_rules_codec_is_never_re_encoded()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossy":{"codec":"mp3","mode":"cbr","bitrateKbps":320}}""");

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky.mp3");

        // The probe measures an MP3: the rule's own codec, so nothing is re-encoded.
        host.Probe.Result = new MediaProbeResult(
            true,
            new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 369_000, false, 5_000_000),
            null);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Transcoder.Requests.Should().BeEmpty();
        host.Verifier.Requests.Should().ContainSingle().Which.SourceQualityId.Should().BeNull();

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(seed.DownloadPath);
        placement.Extension.Should().Be("mp3");
    }

    [Fact]
    public async Task A_keep_rule_imports_a_Soulseek_file_as_the_peer_served_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossy":{"codec":"keep"}}""");

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky.mp3");

        host.Probe.Result = new MediaProbeResult(
            true,
            new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 369_000, false, 5_000_000),
            null);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Transcoder.Requests.Should().BeEmpty();

        var placement = host.Placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(seed.DownloadPath);
        placement.Extension.Should().Be("mp3");
    }

    [Fact]
    public async Task A_converted_file_that_cannot_be_verified_leaves_no_temp_behind()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");

        var seed = await host.SeedAsync();

        // AcoustID is down: the item goes back to the queue, and nothing is counted or deleted.
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Deferred,
            "AcoustID is throttling us",
            null,
            null,
            null,
            null,
            null,
            null,
            false);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        var temp = Path.Combine(seed.DownloadDirectory, "08 - Get Lucky.wondarr-convert.mp3");
        File.Exists(temp).Should().BeFalse("the temporary is deleted on every exit but the placed one");
        File.Exists(seed.DownloadPath).Should().BeTrue("the downloaded original stays for the next attempt");

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.DownloadPath.Should().Be(seed.DownloadPath);
    }

    [Fact]
    public async Task A_compaction_deferral_after_the_conversion_keeps_the_original_as_the_download()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");

        var seed = await host.SeedAsync();

        // A compaction stages the song's file while this import is verifying, after it converted.
        host.Verifier.OnVerifyAsync = async _ =>
        {
            await using var context = host.Database.CreateContext(host.Time);

            context.CompactMoves.Add(new CompactMoveRecord
            {
                LibraryId = SeedData.DefaultLibraryId,
                SongId = seed.SongId,
                FromPath = "/data/music/held.mp3",
                StagedPath = "/data/music/held.mp3.staged",
                ToPath = "/data/music/held.mp3",
                Proposed = "{}",
                State = CompactMoveState.Staged,
            });

            await context.SaveChangesAsync();
        };

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        // The temporary is gone and the item keeps pointing at the downloaded original, so the
        // next pass converts it again instead of importing a file that no longer exists.
        var temp = Path.Combine(seed.DownloadDirectory, "08 - Get Lucky.wondarr-convert.mp3");
        File.Exists(temp).Should().BeFalse();
        File.Exists(seed.DownloadPath).Should().BeTrue();

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.DownloadPath.Should().Be(seed.DownloadPath);
        item.State.Should().Be(QueueItemState.Completed);
    }

    [Fact]
    public async Task Failed_transcode_fails_the_item_without_blocklist()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Transcoder.Failure = new TranscodeException("ffmpeg exited 1: nope");

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        // The transcode is our own step, not the download's: the file passed nothing yet, so there
        // is nothing to blocklist and no reason to grab the next candidate.
        outcome.Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be("Transcode failed: ffmpeg exited 1: nope");

        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().BeEmpty();
        host.Verifier.Requests.Should().BeEmpty();
        host.Placer.Requests.Should().BeEmpty();

        // The Opus original stays on disk, so a later import can try the step again.
        File.Exists(seed.DownloadPath).Should().BeTrue();

        var history = await host.Context.History.Where(entry => entry.SongId == seed.SongId).ToListAsync();
        history.Should().ContainSingle()
            .Which.Data.Should().Contain("Transcode failed: ffmpeg exited 1: nope");
    }

    [Fact]
    public async Task A_failed_conversion_of_a_Soulseek_file_fails_without_deleting_the_original()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");

        var seed = await host.SeedAsync();
        host.Transcoder.Failure = new TranscodeException("ffmpeg exited 1: nope");

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Failed);

        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().BeEmpty();
        host.Verifier.Requests.Should().BeEmpty();

        // The original stays on disk, so a later import can try the conversion again.
        File.Exists(seed.DownloadPath).Should().BeTrue();
    }
}
