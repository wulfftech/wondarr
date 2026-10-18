using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Plays the recorded ffprobe output of every tone file through <see cref="MediaProbe"/> and asserts
/// the exact <see cref="MediaInfo"/> and the quality it measures to (MATCHING_ENGINE.md §6.5 step 1).
/// The fixtures were recorded inside the image: ffmpeg/ffprobe 9.0.2, fpcalc 1.6.1.
/// </summary>
public sealed class MediaProbeTests
{
    /// <summary>One tone file, what ffprobe measured of it, and the quality that measures to.</summary>
    public static TheoryData<string, MediaInfo, long> Tones => new()
    {
        // 320 kbps CBR is exactly the ladder's top tier.
        { "tone-320.mp3", new MediaInfo("mp3", "mp3", 320, 44100, null, 2, 3000, false, 122296), 29 },

        // V0 of a 3 s pure tone measures 107 kbps, which is no CBR tier, so the rules treat it as
        // variable bitrate and give the best CBR tier below it (96 kbps → 11). The task text guessed
        // "30 or 24" for a file that measures above 170 kbps; this fixture does not.
        { "tone-v0.mp3", new MediaInfo("mp3", "mp3", 107, 44100, null, 2, 3000, false, 40462), 11 },

        // Lossless: the container's bitrate is the file's, and the depth is 16 or 24.
        { "tone.flac", new MediaInfo("flac", "flac", 146, 44100, 16, 2, 3000, true, 54706), 36 },
        { "tone-24.flac", new MediaInfo("flac", "flac", 1371, 96000, 24, 2, 1000, true, 171422), 40 },

        // AAC keeps no bit depth, so it is judged on the measured bitrate: 217 kbps is the 180+ tier.
        { "tone-256.m4a", new MediaInfo("aac", "mov", 217, 44100, null, 2, 3000, false, 83432), 19 },

        // Opus has no stream bitrate, so the container's 165 kbps is used: the 144–176 tier.
        { "tone-160.opus", new MediaInfo("opus", "ogg", 165, 48000, null, 2, 3007, false, 62076), 28 },
    };

    [Theory]
    [MemberData(nameof(Tones))]
    public async Task Measures_each_tone_file(string name, MediaInfo expected, long quality)
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read($"{name}.ffprobe.json"))
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));

        var result = await Probe(runner).ProbeAsync($"/m/{name}", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Info.Should().Be(expected);
        MeasuredQuality.FromMediaInfo(result.Info!).Should().Be(quality);
    }

    [Fact]
    public async Task Probes_with_ffprobe_and_then_decodes_the_file()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));

        await Probe(runner).ProbeAsync("/m/tone 320 - one's.mp3", CancellationToken.None);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].FileName.Should().Be("ffprobe");
        runner.Calls[0].Arguments.Should().Equal(
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "/m/tone 320 - one's.mp3");
        runner.Calls[1].FileName.Should().Be("ffmpeg");
        runner.Calls[1].Arguments.Should().Equal(
            "-nostdin", "-v", "error", "-xerror", "-i", "/m/tone 320 - one's.mp3", "-f", "null", "-");
    }

    [Fact]
    public async Task Skips_the_decode_check_when_it_is_off()
    {
        var runner = new FakeProcessRunner().Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"));

        var result = await Probe(runner, new MediaToolsOptions { DecodeCheck = false })
            .ProbeAsync("/m/tone-320.mp3", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reports_an_unreadable_file_with_the_reason_ffmpeg_gave()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(
            1,
            MediaFixtures.Read("garbage.mp3.ffprobe.json"),
            MediaFixtures.Read("garbage.mp3.ffprobe.stderr"),
            TimedOut: false));

        var result = await Probe(runner).ProbeAsync("/m/garbage.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Info.Should().BeNull();
        result.Error.Should().Be("/m/garbage.mp3: Invalid data found when processing input");

        // Nothing is decoded when the probe already failed.
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reports_a_file_with_no_audio_stream()
    {
        var runner = new FakeProcessRunner().Enqueue("{}");

        var result = await Probe(runner).ProbeAsync("/m/cover.jpg", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Error.Should().Be("the file has no audio stream");
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reports_a_file_ffprobe_can_read_but_ffmpeg_cannot_decode()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(new ProcessResult(
                1,
                string.Empty,
                "[mp3 @ 0x55f0] Failed to read frame\n/m/broken.mp3: Invalid data found when processing input\n",
                TimedOut: false));

        var result = await Probe(runner).ProbeAsync("/m/broken.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Error.Should().Be("/m/broken.mp3: Invalid data found when processing input");
    }

    [Fact]
    public async Task Reports_a_probe_that_never_finished()
    {
        var runner = new FakeProcessRunner().Enqueue(
            new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await Probe(runner).ProbeAsync("/m/slow.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Error.Should().Be("ffprobe timed out");
    }

    [Fact]
    public async Task Ignores_a_cover_art_stream_and_measures_the_audio_one()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(
                """
                {
                  "streams": [
                    { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
                    { "codec_type": "audio", "codec_name": "mp3", "sample_rate": "44100", "channels": 2,
                      "bit_rate": "320000", "duration": "3.000000", "disposition": { "attached_pic": 0 } }
                  ],
                  "format": { "format_name": "mp3", "size": "122296" }
                }
                """)
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));

        var result = await Probe(runner).ProbeAsync("/m/tagged.mp3", CancellationToken.None);

        result.Info!.Codec.Should().Be("mp3");
        result.Info.DurationMs.Should().Be(3000);
    }

    [Fact]
    public void Maps_an_unknown_codec_to_the_unknown_quality() =>
        MeasuredQuality
            .FromMediaInfo(new MediaInfo("speex", "ogg", 32, 16000, null, 1, 1000, false, 4000))
            .Should().Be(1);

    [Theory]
    [InlineData("pcm_s16le", "wav", 42)]
    [InlineData("pcm_s24le", "aiff", 43)]
    [InlineData("alac", "mov", 41)]
    [InlineData("wavpack", "wv", 39)]
    [InlineData("ape", "ape", 38)]
    [InlineData("vorbis", "ogg", 20)]
    [InlineData("wmav2", "asf", 21)]
    public void Maps_a_codec_to_the_extension_the_quality_rules_know(string codec, string container, long expected)
    {
        var lossless = codec.StartsWith("pcm", StringComparison.Ordinal) || codec is "alac" or "wavpack" or "ape";

        var quality = MeasuredQuality.FromMediaInfo(new MediaInfo(
            codec,
            container,
            BitrateKbps: 200,
            SampleRate: 44100,
            BitDepth: lossless ? 24 : null,
            Channels: 2,
            DurationMs: 3000,
            IsLossless: lossless,
            SizeBytes: 75000));

        quality.Should().Be(expected);
    }

    // --- Reference files: a few bad frames are tolerated ----------------------------------------
    // tone-320.mp3 is 3 000 ms. The shape of ffmpeg's output for a damaged file was recorded on ch01 with
    // ffmpeg 9.0.2: each bad frame is two or three stderr lines at -v error, and the lenient run exits 0.

    private const string Decoded3s = "out_time_us=3000000\nout_time=00:00:03.000000\nprogress=end\n";

    [Fact]
    public async Task A_reference_file_with_a_few_decode_errors_is_tolerated()
    {
        var runner = DamagedFile(errorLines: 6, progress: Decoded3s);

        var result = await Probe(runner).ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        result.Info.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Tolerated.Should().Be(new ToleratedDecodeErrors(6, 3.0, 3.0));

        runner.Calls.Should().HaveCount(3);
        runner.Calls[1].Arguments.Should().Contain("-xerror");
        runner.Calls[2].FileName.Should().Be("ffmpeg");
        runner.Calls[2].Arguments.Should().Equal(
            "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", "-i", "/m/damaged.mp3", "-f", "null", "-");
    }

    [Fact]
    public async Task The_same_damaged_file_is_still_rejected_for_a_download()
    {
        var runner = DamagedFile(errorLines: 6, progress: Decoded3s);

        var result = await Probe(runner).ProbeAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Tolerated.Should().BeNull();
        result.Error.Should().Be("/m/damaged.mp3: Invalid data found when processing input");

        // The strict probe stops at ffprobe and the -xerror decode: no lenient run.
        runner.Calls.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(MediaProbe.ReferenceMaxErrorLines, true)]
    [InlineData(MediaProbe.ReferenceMaxErrorLines + 1, false)]
    public async Task A_reference_file_may_have_at_most_twenty_error_lines(int errorLines, bool tolerated)
    {
        var runner = DamagedFile(errorLines, Decoded3s);

        var result = await Probe(runner).ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().Be(tolerated);

        if (tolerated)
        {
            result.Tolerated!.Errors.Should().Be(errorLines);
        }
        else
        {
            result.Error.Should().Be("/m/damaged.mp3: Invalid data found when processing input");
            result.Info.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("out_time_us=3000000\nprogress=end\n", true)]
    [InlineData("out_time_us=2950000\nprogress=end\n", true)]
    [InlineData("out_time_us=2930000\nprogress=end\n", false)]
    [InlineData("out_time_us=1500000\nprogress=end\n", false)]
    [InlineData("out_time_us=N/A\nprogress=end\n", false)]
    [InlineData("progress=end\n", false)]
    [InlineData("", false)]
    public async Task A_reference_file_must_decode_at_least_98_percent_of_its_length(string progress, bool tolerated)
    {
        var runner = DamagedFile(errorLines: 3, progress);

        var result = await Probe(runner).ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().Be(tolerated);
    }

    [Fact]
    public async Task The_decoded_time_is_the_last_progress_block()
    {
        var progress = "out_time_us=500000\nprogress=continue\nout_time_us=3000000\nprogress=end\n";

        var result = await Probe(DamagedFile(errorLines: 3, progress))
            .ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Tolerated.Should().Be(new ToleratedDecodeErrors(3, 3.0, 3.0));
    }

    [Fact]
    public async Task A_reference_file_whose_lenient_decode_fails_is_not_tolerated()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(1, "/m/broken.mp3: Invalid data found when processing input\n")
            .Enqueue(new ProcessResult(1, Decoded3s, "/m/broken.mp3: Invalid data found when processing input\n", TimedOut: false));

        var result = await Probe(runner).ProbeReferenceAsync("/m/broken.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Error.Should().Be("/m/broken.mp3: Invalid data found when processing input");
    }

    [Fact]
    public async Task A_reference_file_with_no_duration_is_not_tolerated()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(
                """
                { "streams": [ { "codec_type": "audio", "codec_name": "mp3", "sample_rate": "44100", "channels": 2 } ],
                  "format": { "format_name": "mp3", "size": "122296" } }
                """)
            .Enqueue(1, "[mp3float @ 0x55f0] Header missing\n");

        var result = await Probe(runner).ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        runner.Calls.Should().HaveCount(2, "there is no duration to measure a lenient decode against");
    }

    [Fact]
    public async Task A_reference_file_with_no_audio_stream_is_not_decodable()
    {
        var runner = new FakeProcessRunner().Enqueue("{}");

        var result = await Probe(runner).ProbeReferenceAsync("/m/cover.jpg", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.Error.Should().Be("the file has no audio stream");
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_lenient_decode_that_ran_out_of_time_says_so()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(1, "[mp3float @ 0x55f0] Header missing\n")
            .Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await Probe(runner).ProbeReferenceAsync("/m/slow.mp3", CancellationToken.None);

        result.Decodable.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task A_reference_file_that_decodes_cleanly_is_answered_like_any_other_and_costs_no_extra_run()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));

        var result = await Probe(runner).ProbeReferenceAsync("/m/tone-320.mp3", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        result.Tolerated.Should().BeNull();
        runner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_lenient_decode_that_found_nothing_to_complain_about_is_a_plain_pass()
    {
        // The strict run failed for something that did not come back (a share that hiccuped).
        var runner = DamagedFile(errorLines: 0, Decoded3s);

        var result = await Probe(runner).ProbeReferenceAsync("/m/damaged.mp3", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        result.Tolerated.Should().BeNull();
    }

    [Fact]
    public async Task A_reference_probe_without_the_decode_check_does_not_decode()
    {
        var runner = new FakeProcessRunner().Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"));

        var result = await Probe(runner, new MediaToolsOptions { DecodeCheck = false })
            .ProbeReferenceAsync("/m/tone-320.mp3", CancellationToken.None);

        result.Decodable.Should().BeTrue();
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void Counts_the_lines_ffmpeg_wrote_at_error_level()
    {
        MediaProbe.ErrorLineCount(null).Should().Be(0);
        MediaProbe.ErrorLineCount(" \r\n\n").Should().Be(0);
        MediaProbe.ErrorLineCount("[mp3float @ 0x1] Header missing\r\n[aist#0:0/mp3 @ 0x2] Error submitting packet\r\n")
            .Should().Be(2);
    }

    /// <summary>
    /// ffprobe finds a 3 s MP3, the strict decode stops at the first error, and the lenient decode wrote
    /// <paramref name="errorLines"/> lines and the given <c>-progress</c> output.
    /// </summary>
    private static FakeProcessRunner DamagedFile(int errorLines, string progress) =>
        new FakeProcessRunner()
            .Enqueue(MediaFixtures.Read("tone-320.mp3.ffprobe.json"))
            .Enqueue(1, "[mp3float @ 0x55f0] Header missing\n/m/damaged.mp3: Invalid data found when processing input\n")
            .Enqueue(new ProcessResult(
                0,
                progress,
                string.Concat(Enumerable.Repeat("[mp3float @ 0x55f0] Header missing\n", errorLines)),
                TimedOut: false));

    private static MediaProbe Probe(FakeProcessRunner runner, MediaToolsOptions? options = null) =>
        new(
            runner,
            new TestOptionsMonitor<MediaToolsOptions>(options ?? new MediaToolsOptions()),
            NullLogger<MediaProbe>.Instance);
}
