using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The transcode step of the import pipeline (P4-03): a YouTube download becomes the library's output
/// policy target between the extension check and verification, the Opus original is deleted only
/// after the transcode succeeded, and a failed transcode fails the item without blocklisting it.
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
    public async Task KeepOpus_imports_the_remux()
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
}
