using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// A fact that only runs where the bundled media tools are installed. The dev machines have no ffmpeg;
/// these run inside the image in P2-18 (and on any box with ffprobe and fpcalc on PATH).
/// </summary>
public sealed class MediaToolsFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(HasBothTools);

    /// <summary>Initialises a new instance of the <see cref="MediaToolsFactAttribute"/> class.</summary>
    public MediaToolsFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "ffprobe and fpcalc are not on PATH";
        }
    }

    /// <summary>Whether ffprobe and fpcalc both answer <c>-version</c>.</summary>
    public static bool HasBothTools() => OnPath("ffprobe") && OnPath("fpcalc");

    private static bool OnPath(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>Probes and fingerprints the checked-in tone files with the real binaries.</summary>
public sealed class MediaToolsLiveTests
{
    [MediaToolsFact]
    public async Task Probes_and_fingerprints_a_real_tone_file()
    {
        var path = MediaFixtures.File("tone-320.mp3");

        var probe = await Probe().ProbeAsync(path, CancellationToken.None);

        probe.Decodable.Should().BeTrue();
        probe.Info.Should().NotBeNull();
        probe.Info!.Codec.Should().Be("mp3");
        probe.Info.BitrateKbps.Should().Be(320);
        probe.Info.SampleRate.Should().Be(44100);
        probe.Info.Channels.Should().Be(2);
        probe.Info.BitDepth.Should().BeNull();
        probe.Info.DurationMs.Should().BeInRange(2900, 3100);
        MeasuredQuality.FromMediaInfo(probe.Info).Should().Be(29);

        var fingerprint = await Fingerprinter().FingerprintAsync(
            path,
            FingerprintWindow.Start,
            probe.Info.DurationMs,
            CancellationToken.None);

        fingerprint.Success.Should().BeTrue();
        fingerprint.Fingerprint.Should().NotBeNullOrEmpty();
        fingerprint.DurationSeconds.Should().Be(3);
    }

    [MediaToolsFact]
    public async Task Measures_a_lossless_file_as_lossless_with_its_bit_depth()
    {
        var probe = await Probe().ProbeAsync(MediaFixtures.File("tone-24.flac"), CancellationToken.None);

        probe.Decodable.Should().BeTrue();
        probe.Info!.IsLossless.Should().BeTrue();
        probe.Info.BitDepth.Should().Be(24);
        MeasuredQuality.FromMediaInfo(probe.Info).Should().Be(40);
    }

    [MediaToolsFact]
    public async Task Reports_a_real_garbage_file_as_undecodable()
    {
        var probe = await Probe().ProbeAsync(MediaFixtures.File("garbage.mp3"), CancellationToken.None);

        probe.Decodable.Should().BeFalse();
        probe.Error.Should().Contain("Invalid data found when processing input");
    }

    private static MediaProbe Probe() =>
        new(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<MediaProbe>.Instance);

    private static Fingerprinter Fingerprinter() =>
        new(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<Fingerprinter>.Instance);
}