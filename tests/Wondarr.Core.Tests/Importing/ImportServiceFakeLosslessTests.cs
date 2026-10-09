using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The fake-lossless check of the import (P8-01): a lossless download whose spectrum stops below
/// 19.5 kHz is refused and blocklisted for the song, so the next candidate is tried.
/// </summary>
public sealed class ImportServiceFakeLosslessTests
{
    private static SpectralVerdict Lossy(double cutoffHz) => new(SpectralOutcome.Lossy, cutoffHz, 1200);

    [Fact]
    public async Task A_lossless_download_with_a_lossy_spectrum_is_rejected_and_blocklisted()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Spectral.Verdict = Lossy(16_125);
        host.Search.Next = 77;

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Rejected);

        var request = host.Spectral.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be(seed.DownloadPath);
        request.DurationMs.Should().Be(FakeMediaProbe.Flac().DurationMs);

        const string Reason = "Fake lossless: the spectrum stops at 16.1 kHz (a lossy source)";

        var blocked = await host.Context.Blocklist.SingleAsync();
        blocked.SongId.Should().Be(seed.SongId);
        blocked.BlocklistKey.Should().Be(seed.BlocklistKey);
        blocked.Reason.Should().Be(Reason);

        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be(Reason);

        host.Search.Grabs.Should().Equal((seed.SearchRunId, 2));
        host.Verifier.Requests.Should().BeEmpty("the file is refused before it is verified");
        host.Placer.Requests.Should().BeEmpty();

        var history = await host.Context.History
            .Where(entry => entry.SongId == seed.SongId && entry.EventType == HistoryEventType.Rejected)
            .SingleAsync();
        history.QualityId.Should().Be(MeasuredQuality.FromMediaInfo(FakeMediaProbe.Flac()));
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("reason").GetString().Should().Be(Reason);
    }

    [Fact]
    public async Task A_genuine_lossless_download_imports_as_before()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Spectral.Requests.Should().ContainSingle();
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_inconclusive_spectrum_imports_as_before()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Spectral.Verdict = new SpectralVerdict(SpectralOutcome.Inconclusive, null, 0);
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
    }

    [Fact]
    public async Task With_the_check_off_the_analyzer_is_never_called()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.ImportOptions.FakeLosslessCheck = FakeLosslessCheck.Off;
        host.Spectral.Verdict = Lossy(16_125);
        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);
        host.Spectral.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lossy_download_is_never_analysed()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Probe.Result = new MediaProbeResult(
            true,
            new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 369_000, false, 14_000_000),
            null);
        host.Spectral.Verdict = Lossy(16_125);
        var seed = await host.SeedAsync();

        await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        host.Spectral.Requests.Should().BeEmpty();
    }
}
