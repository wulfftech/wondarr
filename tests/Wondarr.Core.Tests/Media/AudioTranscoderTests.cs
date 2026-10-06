using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Wondarr.Core.Profiles;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// The transcode step: the ffmpeg arguments each policy produces, the no-<c>-y</c> rule, the
/// shared timeout, and the targets that are refused outright.
/// </summary>
public sealed class AudioTranscoderTests
{
    private static readonly string Source = Path.Combine(Path.GetTempPath(), "wondarr-transcoder-tests", "source.opus");

    private static Transcoder Transcoder(FakeProcessRunner runner, MediaToolsOptions? options = null) => new(
        runner,
        new TestOptionsMonitor<MediaToolsOptions>(options ?? new MediaToolsOptions()),
        NullLogger<Transcoder>.Instance);

    [Fact]
    public async Task Keeping_the_opus_remux_runs_nothing()
    {
        var runner = new FakeProcessRunner();
        var transcoder = Transcoder(runner);

        var result = await transcoder.TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"codec":"keepOpus"}"""),
            Path.ChangeExtension(Source, ".m4a"),
            CancellationToken.None);

        result.Path.Should().Be(Source);
        result.Extension.Should().Be("opus");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_aac_policy_encodes_at_a_constant_bitrate()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        var target = Path.ChangeExtension(Source, ".m4a");

        var result = await Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"codec":"aac","bitrateKbps":256}"""),
            target,
            CancellationToken.None);

        result.Path.Should().Be(target);
        result.Extension.Should().Be("m4a");

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().ContainInOrder("-i", Source, "-vn", "-c:a", "aac", "-b:a", "256k", target);
        call.Arguments.Should().NotContain("-y");
        call.Timeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task An_mp3_cbr_policy_uses_lame_at_a_constant_bitrate()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        var target = Path.ChangeExtension(Source, ".mp3");

        await Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"codec":"mp3","mode":"cbr","bitrateKbps":320}"""),
            target,
            CancellationToken.None);

        runner.Calls[0].Arguments.Should().ContainInOrder("-c:a", "libmp3lame", "-b:a", "320k");
    }

    [Fact]
    public async Task An_mp3_vbr_policy_uses_the_lame_quality_scale()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        var target = Path.ChangeExtension(Source, ".mp3");

        await Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"codec":"mp3","mode":"vbr","vbrQuality":2}"""),
            target,
            CancellationToken.None);

        runner.Calls[0].Arguments.Should().ContainInOrder("-c:a", "libmp3lame", "-q:a", "2");
        runner.Calls[0].Arguments.Should().NotContain("-b:a");
    }

    [Fact]
    public async Task A_sample_rate_is_written_with_the_ar_switch()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        var target = Path.ChangeExtension(Source, ".m4a");

        await Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"sampleRate":44100}"""),
            target,
            CancellationToken.None);

        runner.Calls[0].Arguments.Should().ContainInOrder("-b:a", "256k", "-ar", "44100", target);
    }

    [Fact]
    public async Task A_lossless_target_is_refused_before_anything_runs()
    {
        var runner = new FakeProcessRunner();
        var target = Path.ChangeExtension(Source, ".flac");

        var act = () => Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Parse("""{"codec":"aac"}"""),
            target,
            CancellationToken.None);

        await act.Should().ThrowAsync<TranscodePolicyException>();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_encoder_run_is_a_transcode_exception()
    {
        var runner = new FakeProcessRunner().Enqueue(1, "Invalid argument: -b:a");

        var act = () => Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Default,
            Path.ChangeExtension(Source, ".m4a"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TranscodeException>()
            .WithMessage("ffmpeg exited 1: Invalid argument: -b:a*");
    }

    [Fact]
    public async Task A_timed_out_encoder_run_is_a_transcode_exception()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var act = () => Transcoder(runner).TranscodeAsync(
            Source,
            OutputPolicy.Default,
            Path.ChangeExtension(Source, ".m4a"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TranscodeException>()
            .WithMessage("ffmpeg was killed after 120 s:*");
    }

    [Fact]
    public async Task The_ffmpeg_path_comes_from_the_media_options()
    {
        var runner = new FakeProcessRunner().Enqueue(string.Empty);
        var options = new MediaToolsOptions { FfmpegPath = "/opt/ffmpeg", TimeoutSeconds = 30 };

        await Transcoder(runner, options).TranscodeAsync(
            Source,
            OutputPolicy.Default,
            Path.ChangeExtension(Source, ".m4a"),
            CancellationToken.None);

        runner.Calls[0].FileName.Should().Be("/opt/ffmpeg");
        runner.Calls[0].Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
