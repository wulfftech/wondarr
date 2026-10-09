using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Plays recorded <c>ebur128</c> stderr through <see cref="ReplayGainAnalyzer"/>: the gain is
/// -18 LUFS minus the integrated loudness, the peak the true peak as a linear value, and anything
/// that is not a measurement is <see langword="null"/>.
/// </summary>
public sealed class ReplayGainAnalyzerTests
{
    // The summary ffmpeg's ebur128 filter prints when peak=true, after per-frame lines that carry
    // their own "I:" and "TPK:" (so the parser has to read only the summary).
    private const string NormalTrack = """
        ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers
        Input #0, flac, from 'song.flac':
          Duration: 00:03:29.00, start: 0.000000, bitrate: 912 kb/s
        [Parsed_ebur128_0 @ 0x600003a1c000] t: 0.0999977  TARGET:-23 LUFS    M: -24.1 S:-120.7     I: -24.1 LUFS       LRA:   0.0 LU  FTPK: -9.8  -9.9 dBFS  TPK: -9.8  -9.9 dBFS
        [Parsed_ebur128_0 @ 0x600003a1c000] t: 0.199998  TARGET:-23 LUFS    M: -18.2 S:-120.7     I: -20.0 LUFS       LRA:   0.0 LU  FTPK: -5.1  -5.2 dBFS  TPK: -5.1  -5.2 dBFS
        [Parsed_ebur128_0 @ 0x600003a1c000] Summary:

          Integrated loudness:
            I:          -9.5 LUFS
            Threshold: -19.6 LUFS

          Loudness range:
            LRA:         3.2 LU
            Threshold: -29.6 LUFS
            LRA low:   -11.9 LUFS
            LRA high:   -8.7 LUFS

          True peak:
            Peak:        0.4 dBFS
        size=N/A time=00:03:29.00 bitrate=N/A speed= 150x
        """;

    private const string Silence = """
        [Parsed_ebur128_0 @ 0x600003a1c000] Summary:

          Integrated loudness:
            I:         -70.0 LUFS
            Threshold: -80.0 LUFS

          Loudness range:
            LRA:         0.0 LU
            Threshold: -90.0 LUFS
            LRA low:   -70.0 LUFS
            LRA high:  -70.0 LUFS

          True peak:
            Peak:       -inf dBFS
        """;

    [Fact]
    public void A_normal_track_has_the_gain_against_minus_18_and_the_peak_as_a_linear_value()
    {
        var values = ReplayGainAnalyzer.Parse(NormalTrack);

        values.Should().NotBeNull();
        values!.GainDb.Should().Be(-8.5, "-18 - (-9.5)");
        values.Peak.Should().Be(1.047129, "10^(0.4/20)");
    }

    [Fact]
    public void A_quiet_track_gets_a_positive_gain_rounded_to_two_decimals()
    {
        var values = ReplayGainAnalyzer.Parse(NormalTrack.Replace("-9.5 LUFS", "-23.456 LUFS", StringComparison.Ordinal));

        values!.GainDb.Should().Be(5.46);
    }

    [Fact]
    public void Silence_is_not_a_measurement()
    {
        ReplayGainAnalyzer.Parse(Silence).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Output file is empty, nothing was encoded")]
    public void Output_without_a_summary_is_not_a_measurement(string stderr)
    {
        ReplayGainAnalyzer.Parse(stderr).Should().BeNull();
    }

    [Fact]
    public async Task Runs_ffmpeg_on_the_first_audio_stream_and_parses_the_summary()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(0, string.Empty, NormalTrack, TimedOut: false));

        var values = await Analyzer(runner).MeasureAsync("/m/song.flac", CancellationToken.None);

        values.Should().Be(new ReplayGainValues(-8.5, 1.047129));
        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().Equal(
            "-nostdin", "-hide_banner", "-nostats", "-i", "/m/song.flac", "-map", "0:a:0", "-af", "ebur128=peak=true", "-f", "null", "-");
    }

    [Fact]
    public async Task A_non_zero_exit_is_null()
    {
        var runner = new FakeProcessRunner().Enqueue(1, "song.flac: Invalid data found when processing input");

        (await Analyzer(runner).MeasureAsync("/m/song.flac", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_timeout_is_null()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, NormalTrack, TimedOut: true));

        (await Analyzer(runner).MeasureAsync("/m/song.flac", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_missing_ffmpeg_is_null()
    {
        var runner = new FakeProcessRunner().EnqueueMissing("ffmpeg");

        (await Analyzer(runner).MeasureAsync("/m/song.flac", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Silence_from_a_successful_run_is_null()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(0, string.Empty, Silence, TimedOut: false));

        (await Analyzer(runner).MeasureAsync("/m/song.flac", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();

        var act = () => Analyzer(new CancellingRunner()).MeasureAsync("/m/song.flac", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ReplayGainAnalyzer Analyzer(IProcessRunner runner) =>
        new(runner, new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()), NullLogger<ReplayGainAnalyzer>.Instance);

    private sealed class CancellingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
