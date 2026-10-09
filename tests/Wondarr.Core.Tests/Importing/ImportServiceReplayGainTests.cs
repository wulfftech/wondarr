using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>ReplayGain at import: measured on the placed file when the library opts in, never otherwise.</summary>
public sealed class ImportServiceReplayGainTests
{
    [Fact]
    public async Task A_library_with_replaygain_on_measures_the_file_and_tags_and_stores_the_values()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryReplayGainAsync(true);
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.ReplayGain.Measured.Should().Equal(seed.DownloadPath);
        var tags = host.TagWriter.Writes.Should().ContainSingle().Subject.Tags;
        tags.ReplayGainTrackGainDb.Should().Be(-8.52);
        tags.ReplayGainTrackPeak.Should().Be(1.047129);
        var file = await host.Context.SongFiles.SingleAsync();
        file.ReplayGainDb.Should().Be(-8.52);
        file.ReplayGainPeak.Should().Be(1.047129);
    }

    [Theory]
    [InlineData(true, -8.52)]
    [InlineData(false, null)]
    public async Task An_upgrade_replaces_the_old_values(bool on, double? expected)
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryReplayGainAsync(on);
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
            options.CurrentFileQualityId = 29;
        });
        await host.Context.SongFiles.ExecuteUpdateAsync(update => update
            .SetProperty(file => file.ReplayGainDb, -1.0)
            .SetProperty(file => file.ReplayGainPeak, 0.25));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Upgraded);
        var file = await host.Context.SongFiles.AsNoTracking().SingleAsync();
        file.ReplayGainDb.Should().Be(expected);
        file.ReplayGainPeak.Should().Be(on ? 1.047129 : null);
    }

    [Fact]
    public async Task A_library_with_replaygain_off_never_measures()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.ReplayGain.Measured.Should().BeEmpty();
        var tags = host.TagWriter.Writes.Should().ContainSingle().Subject.Tags;
        tags.ReplayGainTrackGainDb.Should().BeNull();
        tags.ReplayGainTrackPeak.Should().BeNull();
        var file = await host.Context.SongFiles.SingleAsync();
        file.ReplayGainDb.Should().BeNull();
        file.ReplayGainPeak.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_measurement_still_imports_the_file_without_the_tags()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryReplayGainAsync(true);
        host.ReplayGain.Values = null;
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.ReplayGain.Measured.Should().ContainSingle();
        host.TagWriter.Writes.Should().ContainSingle().Which.Tags.ReplayGainTrackGainDb.Should().BeNull();
        var file = await host.Context.SongFiles.SingleAsync();
        file.ReplayGainDb.Should().BeNull();
        file.ReplayGainPeak.Should().BeNull();
    }

    [Fact]
    public async Task The_converted_file_is_the_one_measured()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryReplayGainAsync(true);
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"mp3","mode":"cbr","bitrateKbps":320}}""");
        var seed = await host.SeedAsync();
        host.Verifier.Result = FakeDownloadVerifier.Passed(
            media: new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 369_000, false, 5_000_000));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        var converted = host.Transcoder.Requests.Should().ContainSingle().Subject.DestinationPath;
        host.ReplayGain.Measured.Should().Equal(converted);
    }
}
