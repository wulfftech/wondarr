using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary><see cref="MediaProbe.ProbeVideoAsync"/>: what it reads out of ffprobe's JSON for a music video.</summary>
public sealed class MediaProbeVideoTests
{
    private const string Mkv = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080, "duration": "284.100000" },
            { "index": 1, "codec_name": "opus", "codec_type": "audio", "sample_rate": "48000", "channels": 2 }
          ],
          "format": { "format_name": "matroska,webm", "duration": "284.081000", "size": "52428800", "bit_rate": "1476000" }
        }
        """;

    [Fact]
    public async Task It_reads_the_streams_the_picture_size_the_codecs_the_length_and_the_size()
    {
        var runner = new FakeProcessRunner().Enqueue(Mkv);

        var result = await Probe(runner).ProbeVideoAsync("/v/teardrop.mkv", CancellationToken.None);

        result.Readable.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Info.Should().Be(new VideoInfo(1, 1, 1920, 1080, "h264", "opus", 284_081, 52_428_800));

        var call = runner.Calls.Should().ContainSingle("no decode check is run on a video").Subject;
        call.FileName.Should().Be("ffprobe");
        call.Arguments.Should().Contain("-show_streams").And.Contain("/v/teardrop.mkv");
    }

    [Fact]
    public async Task A_cover_picture_attached_as_a_video_stream_is_not_a_video()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_name": "opus", "codec_type": "audio" },
                { "index": 1, "codec_name": "mjpeg", "codec_type": "video", "width": 500, "height": 500, "disposition": { "attached_pic": 1 } }
              ],
              "format": { "duration": "10.0", "size": "100" }
            }
            """;

        var result = await Probe(new FakeProcessRunner().Enqueue(json)).ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Readable.Should().BeTrue();
        result.Info!.VideoStreams.Should().Be(0);
        result.Info.AudioStreams.Should().Be(1);
    }

    [Fact]
    public async Task A_file_with_no_audio_reports_no_audio_stream()
    {
        const string json = """
            {
              "streams": [ { "index": 0, "codec_name": "vp9", "codec_type": "video", "width": 640, "height": 360 } ],
              "format": { "duration": "10.0", "size": "100" }
            }
            """;

        var result = await Probe(new FakeProcessRunner().Enqueue(json)).ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Info!.VideoStreams.Should().Be(1);
        result.Info.AudioStreams.Should().Be(0);
        result.Info.AudioCodec.Should().BeNull();
    }

    [Fact]
    public async Task A_file_ffprobe_cannot_read_is_not_readable()
    {
        var runner = new FakeProcessRunner().Enqueue(1, "/v/a.mkv: Invalid data found when processing input");

        var result = await Probe(runner).ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Readable.Should().BeFalse();
        result.Info.Should().BeNull();
        result.Error.Should().Contain("Invalid data");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task A_tool_that_ran_out_of_time_is_flagged()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await Probe(runner).ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Readable.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task Output_that_is_not_json_is_not_readable()
    {
        var result = await Probe(new FakeProcessRunner().Enqueue("not json")).ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Readable.Should().BeFalse();
    }

    [Fact]
    public async Task A_probe_that_cannot_read_video_says_so()
    {
        IMediaProbe probe = new AudioOnlyProbe();

        var result = await probe.ProbeVideoAsync("/v/a.mkv", CancellationToken.None);

        result.Readable.Should().BeFalse();
        result.Error.Should().Contain("cannot read video");
    }

    private static MediaProbe Probe(FakeProcessRunner runner) =>
        new(runner, new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()), NullLogger<MediaProbe>.Instance);

    private sealed class AudioOnlyProbe : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
