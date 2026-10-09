using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>The decode half of the spectral check: the ffmpeg window, the temp file and the failure modes.</summary>
public sealed class SpectralAnalyzerTests
{
    private const string Source = "/music/song.flac";

    private static SpectralAnalyzer Analyzer(FakeProcessRunner runner) => new(
        runner,
        new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
        NullLogger<SpectralAnalyzer>.Instance);

    /// <summary>Makes the fake ffmpeg write <paramref name="samples"/> to the output path it was given.</summary>
    private static void WriteOutput(ProcessCall call, float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(call.Arguments[^1], bytes);
    }

    [Theory]
    [InlineData(240_000, "72")]
    [InlineData(70_000, "10")]
    public async Task A_long_file_is_read_from_30_percent_moved_so_the_window_ends_inside_it(int durationMs, string start)
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call => WriteOutput(call, new float[SpectrumAnalysis.FrameLength]);

        await Analyzer(runner).AnalyzeAsync(Source, durationMs, CancellationToken.None);

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Take(call.Arguments.Count - 1).Should().Equal(
            "-nostdin", "-v", "error", "-ss", start, "-t", "60", "-i", Source,
            "-map", "0:a:0", "-ac", "1", "-ar", "44100", "-f", "f32le", "-y");
    }

    [Fact]
    public async Task A_short_file_is_read_whole()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call => WriteOutput(call, new float[SpectrumAnalysis.FrameLength]);

        await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().NotContain("-ss");
        call.Arguments.Take(call.Arguments.Count - 1).Should().Equal(
            "-nostdin", "-v", "error", "-t", "60", "-i", Source,
            "-map", "0:a:0", "-ac", "1", "-ar", "44100", "-f", "f32le", "-y");
    }

    [Fact]
    public async Task The_output_is_read_and_the_temp_file_removed()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            WriteOutput(call, new float[SpectrumAnalysis.FrameLength * 4]);
        };

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        // All silence: read and analysed, and too little signal to judge.
        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        output.Should().NotBeNull();
        output!.Should().StartWith(Path.GetTempPath());
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task A_non_zero_exit_is_inconclusive_and_leaves_nothing_behind()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(1, "first line\nlast line");
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            WriteOutput(call, new float[SpectrumAnalysis.FrameLength]);
        };

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task The_window_is_read_into_the_analysis_and_a_partial_sample_dropped()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            WriteOutput(call, SpectrumAnalysisTests.LowPassedNoise(cutoffHz: 16_000));

            // Three stray bytes after the last whole sample, as a cut-off write would leave.
            using var stream = new FileStream(output, FileMode.Append);
            stream.Write([1, 2, 3]);
        };

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Lossy);
        verdict.CutoffHz.Should().BeInRange(15_750, 16_250);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task A_timeout_is_inconclusive()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            WriteOutput(call, new float[SpectrumAnalysis.FrameLength]);
        };

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Any_other_failure_is_inconclusive_not_an_exception()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = _ => throw new InvalidOperationException("the process would not start");

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
    }

    [Fact]
    public async Task Empty_output_is_inconclusive_and_removed()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            File.WriteAllBytes(output, []);
        };

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task No_output_file_is_inconclusive()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call => output = call.Arguments[^1];

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_ffmpeg_is_inconclusive_not_an_exception()
    {
        var runner = new FakeProcessRunner().EnqueueMissing("ffmpeg");

        var verdict = await Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
    }

    [Fact]
    public async Task Cancellation_propagates_and_the_temp_file_is_removed()
    {
        string? output = null;
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        runner.OnCall = call =>
        {
            output = call.Arguments[^1];
            WriteOutput(call, new float[SpectrumAnalysis.FrameLength]);
            throw new OperationCanceledException();
        };

        var act = () => Analyzer(runner).AnalyzeAsync(Source, 45_000, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
    }
}
