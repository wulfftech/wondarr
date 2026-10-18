using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Plays the recorded fpcalc output of the tone files through <see cref="Fingerprinter"/>, and watches
/// the temporary WAV the middle window is cut into (MATCHING_ENGINE.md §6.5 step 3).
/// </summary>
public sealed class FingerprinterTests
{
    /// <summary>Where fpcalc was pointing when the middle window was fingerprinted.</summary>
    private static string? _temporarySeenByFpcalc;

    [Fact]
    public async Task Fingerprints_the_start_of_the_file_with_the_configured_length()
    {
        var runner = new FakeProcessRunner().Enqueue(MediaFixtures.Read("tone-320.mp3.fpcalc.json"));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/tone-320.mp3", FingerprintWindow.Start, 369999, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Fingerprint.Should().Be("AQAAA0mUaEkSZSoA");
        result.Window.Should().Be(FingerprintWindow.Start);
        result.Error.Should().BeNull();

        // AcoustID is told the whole track's length, truncated — never fpcalc's own "duration": 3.0.
        result.DurationSeconds.Should().Be(369);

        runner.Calls.Should().HaveCount(1);
        runner.Calls[0].FileName.Should().Be("fpcalc");
        runner.Calls[0].Arguments.Should().Equal("-json", "-length", "120", "/m/tone-320.mp3");
    }

    [Fact]
    public async Task Honors_a_configured_fingerprint_length()
    {
        var runner = new FakeProcessRunner().Enqueue(MediaFixtures.Read("tone.flac.fpcalc.json"));

        await Fingerprinter(runner, new MediaToolsOptions { FingerprintLengthSeconds = 200 })
            .FingerprintAsync("/m/tone.flac", FingerprintWindow.Start, 3000, CancellationToken.None);

        runner.Calls[0].Arguments.Should().Equal("-json", "-length", "200", "/m/tone.flac");
    }

    [Fact]
    public async Task Reports_a_fingerprint_that_fpcalc_refused_to_make()
    {
        // ffmpeg cannot cut a window out of it either, so the answer is fpcalc's own.
        var runner = new FakeProcessRunner { OnCall = CreateWindow }
            .Enqueue(new ProcessResult(2, string.Empty, MediaFixtures.Read("garbage.mp3.fpcalc.stderr"), TimedOut: false))
            .Enqueue(1, "/m/garbage.mp3: Invalid data found when processing input\n");

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/garbage.mp3", FingerprintWindow.Start, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Fingerprint.Should().BeNull();
        result.Error.Should().Be("ERROR: Could not open the input file (Invalid data found when processing input)");
        result.Window.Should().Be(FingerprintWindow.Start);
        runner.Calls.Should().HaveCount(2);
    }

    // fpcalc 1.6.1 stops at the first bad frame of a damaged MP3: exit 3 with a stub of a fingerprint when the
    // error comes after some audio, exit 2 "Empty fingerprint" when it comes first. Recorded on ch01.
    private const string StubFingerprint = "{\"duration\": 30.00, \"fingerprint\": \"AQAAKUmUaEkSZSoAAAAAAAAAAAAAAAAAAAAA\"}\n";

    private const string ReadError = "ERROR: Error reading from the audio source (Invalid data found when processing input)\n";

    [Theory]
    [InlineData(3, StubFingerprint, ReadError)]
    [InlineData(2, "", ReadError + "ERROR: Empty fingerprint\n")]
    public async Task Cuts_the_start_with_ffmpeg_when_fpcalc_cannot_read_the_file(
        int exitCode,
        string standardOutput,
        string standardError)
    {
        _temporarySeenByFpcalc = null;

        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner
            .Enqueue(new ProcessResult(exitCode, standardOutput, standardError, TimedOut: false))
            .Enqueue(new ProcessResult(0, string.Empty, "[mp3float @ 0x1] Header missing\n", TimedOut: false))
            .Enqueue(MediaFixtures.Read("tone-320.mp3.fpcalc.json"));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/damaged.mp3", FingerprintWindow.Start, 369999, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Fingerprint.Should().Be("AQAAA0mUaEkSZSoA");
        result.Window.Should().Be(FingerprintWindow.Start);
        result.DurationSeconds.Should().Be(369);
        result.Error.Should().BeNull();

        // fpcalc on the file, then ONE ffmpeg cut from 0 with the same length, then fpcalc on the cut.
        runner.Calls.Should().HaveCount(3);
        var temporary = runner.Calls[1].Arguments[^1];

        runner.Calls[0].FileName.Should().Be("fpcalc");
        runner.Calls[0].Arguments.Should().Equal("-json", "-length", "120", "/m/damaged.mp3");
        runner.Calls[1].FileName.Should().Be("ffmpeg");
        runner.Calls[1].Arguments.Should().Equal(
            "-nostdin", "-v", "error", "-ss", "0", "-t", "120", "-i", "/m/damaged.mp3",
            "-ac", "2", "-ar", "44100", "-f", "wav", temporary);
        runner.Calls[2].FileName.Should().Be("fpcalc");
        runner.Calls[2].Arguments.Should().Equal("-json", temporary);
        _temporarySeenByFpcalc.Should().Be(temporary);
        File.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task The_fallback_cuts_as_long_a_window_as_the_direct_run_asked_for()
    {
        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner
            .Enqueue(3, ReadError)
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false))
            .Enqueue(MediaFixtures.Read("tone-320.mp3.fpcalc.json"));

        await Fingerprinter(runner, new MediaToolsOptions { FingerprintLengthSeconds = 200 })
            .FingerprintAsync("/m/damaged.mp3", FingerprintWindow.Start, 369999, CancellationToken.None);

        runner.Calls[0].Arguments.Should().Equal("-json", "-length", "200", "/m/damaged.mp3");
        runner.Calls[1].Arguments.Should().ContainInOrder("-ss", "0", "-t", "200");
    }

    [Fact]
    public async Task Gives_fpcalcs_own_failure_when_the_fallback_fails_too()
    {
        _temporarySeenByFpcalc = null;

        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner
            .Enqueue(3, ReadError)
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false))
            .Enqueue(2, "ERROR: Empty fingerprint\n");

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/damaged.mp3", FingerprintWindow.Start, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("ERROR: Error reading from the audio source (Invalid data found when processing input)");
        result.Window.Should().Be(FingerprintWindow.Start);
        runner.Calls.Should().HaveCount(3, "one second try, never more");
        File.Exists(runner.Calls[1].Arguments[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Gives_fpcalcs_own_failure_when_ffmpeg_is_not_installed()
    {
        var runner = new FakeProcessRunner().Enqueue(3, ReadError).EnqueueMissing("ffmpeg");

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/damaged.mp3", FingerprintWindow.Start, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("ERROR: Error reading from the audio source (Invalid data found when processing input)");
    }

    [Fact]
    public async Task Does_not_try_again_when_fpcalc_ran_out_of_time()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/slow.flac", FingerprintWindow.Start, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        runner.Calls.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(369999, "124")]
    [InlineData(3000, "0")]
    [InlineData(240000, "60")]
    public async Task Cuts_the_middle_window_out_of_the_file(int trackDurationMs, string expectedStart)
    {
        _temporarySeenByFpcalc = null;

        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false))
            .Enqueue(MediaFixtures.Read("tone-320.mp3.fpcalc.json"));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/tone-320.mp3", FingerprintWindow.Middle, trackDurationMs, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Window.Should().Be(FingerprintWindow.Middle);
        result.DurationSeconds.Should().Be(trackDurationMs / 1000);
        result.Fingerprint.Should().Be("AQAAA0mUaEkSZSoA");

        runner.Calls.Should().HaveCount(2);
        var temporary = runner.Calls[0].Arguments[^1];

        runner.Calls[0].FileName.Should().Be("ffmpeg");
        runner.Calls[0].Arguments.Should().Equal(
            "-nostdin", "-v", "error", "-ss", expectedStart, "-t", "120", "-i", "/m/tone-320.mp3",
            "-ac", "2", "-ar", "44100", "-f", "wav", temporary);

        // The cut-out window is what fpcalc is handed, and it is gone once the call returns.
        runner.Calls[1].FileName.Should().Be("fpcalc");
        runner.Calls[1].Arguments.Should().Equal("-json", temporary);
        _temporarySeenByFpcalc.Should().Be(temporary);
        temporary.Should().StartWith(Path.GetTempPath());
        File.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task Deletes_the_temporary_window_when_fpcalc_fails()
    {
        _temporarySeenByFpcalc = null;

        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner
            .Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false))
            .Enqueue(new ProcessResult(2, string.Empty, "ERROR: Empty fingerprint\n", TimedOut: false));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/tone.flac", FingerprintWindow.Middle, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("ERROR: Empty fingerprint");

        var temporary = runner.Calls[0].Arguments[^1];
        _temporarySeenByFpcalc.Should().Be(temporary);
        File.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task Deletes_the_temporary_window_when_ffmpeg_cannot_cut_it()
    {
        var runner = new FakeProcessRunner { OnCall = CreateWindow };
        runner.Enqueue(new ProcessResult(
            1,
            string.Empty,
            "/m/tone.flac: Invalid data found when processing input\n",
            TimedOut: false));

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/tone.flac", FingerprintWindow.Middle, 60000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("/m/tone.flac: Invalid data found when processing input");

        // fpcalc was never reached, and the half-written window is gone.
        runner.Calls.Should().HaveCount(1);
        File.Exists(runner.Calls[0].Arguments[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_output_that_is_not_a_fingerprint()
    {
        var runner = new FakeProcessRunner { OnCall = CreateWindow }
            .Enqueue("{\"duration\": 3.0}")
            .Enqueue(1, "/m/tone.flac: Invalid data found when processing input\n");

        var result = await Fingerprinter(runner)
            .FingerprintAsync("/m/tone.flac", FingerprintWindow.Start, 3000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("fpcalc returned no fingerprint");
    }

    /// <summary>Writes the window ffmpeg was told to produce, as ffmpeg would.</summary>
    private static void CreateWindow(ProcessCall call)
    {
        if (call.FileName == "ffmpeg")
        {
            File.WriteAllBytes(call.Arguments[^1], [0x52, 0x49, 0x46, 0x46]);
        }
        else if (call.FileName == "fpcalc" && call.Arguments.Count == 2)
        {
            _temporarySeenByFpcalc = call.Arguments[1];
        }
    }

    private static Fingerprinter Fingerprinter(FakeProcessRunner runner, MediaToolsOptions? options = null) =>
        new(
            runner,
            new TestOptionsMonitor<MediaToolsOptions>(options ?? new MediaToolsOptions()),
            NullLogger<Fingerprinter>.Instance);
}
