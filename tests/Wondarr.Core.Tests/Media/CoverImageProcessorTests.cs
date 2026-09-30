using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Bounding a cover is a best-effort step on the way to a tag write: it must never make an import
/// fail, and it must never leave its temporary files behind.
/// </summary>
public sealed class CoverImageProcessorTests
{
    private static readonly byte[] JpegOutput = [0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22];

    private readonly FakeProcessRunner _runner = new();
    private readonly MediaToolsOptions _options = new();

    private static string Cover(string name) => MediaFixtures.File($"covers/{name}");

    [Fact]
    public void Reads_the_dimensions_of_a_jpeg()
    {
        CoverImageProcessor.ReadDimensions(File.ReadAllBytes(Cover("cover-1600x1600.jpg")))
            .Should().Be((1600, 1600));
        CoverImageProcessor.ReadDimensions(File.ReadAllBytes(Cover("cover-300x300.jpg")))
            .Should().Be((300, 300));
    }

    [Fact]
    public void Reads_the_dimensions_of_a_png()
    {
        CoverImageProcessor.ReadDimensions(File.ReadAllBytes(Cover("cover-600x400.png")))
            .Should().Be((600, 400));
    }

    [Fact]
    public void Returns_null_for_a_truncated_jpeg()
    {
        var truncated = File.ReadAllBytes(Cover("cover-1600x1600.jpg"))[..20];

        CoverImageProcessor.ReadDimensions(truncated).Should().BeNull();
        CoverImageProcessor.ReadDimensions([0xFF, 0xD8, 0xFF, 0xE0]).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_bytes_that_are_not_an_image()
    {
        CoverImageProcessor.ReadDimensions([1, 2, 3, 4, 5]).Should().BeNull();
        CoverImageProcessor.ReadDimensions(new byte[64]).Should().BeNull();
        CoverImageProcessor.ReadDimensions("not an image at all, not even close"u8).Should().BeNull();
    }

    [Fact]
    public async Task Returns_a_small_jpeg_unchanged_without_running_ffmpeg()
    {
        var image = File.ReadAllBytes(Cover("cover-300x300.jpg"));

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().BeSameAs(image);
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Runs_ffmpeg_with_the_expected_arguments_and_returns_what_it_wrote()
    {
        var image = File.ReadAllBytes(Cover("cover-1600x1600.jpg"));
        string? temporary = null;

        _runner.Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));
        _runner.OnCall = call =>
        {
            temporary = Path.GetDirectoryName(call.Arguments[^1]);
            File.WriteAllBytes(call.Arguments[^1], JpegOutput);
        };

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().Equal(JpegOutput);

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be(_options.FfmpegPath);
        call.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        call.Arguments.Should().Equal(
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", Path.Combine(temporary!, "in.jpg"),
            "-vf", "scale=1400:1400:flags=lanczos",
            "-frames:v", "1",
            "-q:v", "2",
            Path.Combine(temporary!, "out.jpg"));

        Directory.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task Converts_a_png_that_is_within_bounds_to_a_jpeg()
    {
        var image = File.ReadAllBytes(Cover("cover-600x400.png"));
        string? temporary = null;

        _runner.Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));
        _runner.OnCall = call =>
        {
            temporary = Path.GetDirectoryName(call.Arguments[^1]);
            File.WriteAllBytes(call.Arguments[^1], JpegOutput);
        };

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().Equal(JpegOutput);
        _runner.Calls.Should().ContainSingle().Which.Arguments.Should()
            .ContainInOrder("-vf", "scale=600:400:flags=lanczos")
            .And.Contain(Path.Combine(temporary!, "in.png"));
    }

    [Fact]
    public async Task Keeps_the_aspect_ratio_of_an_unknown_size_with_ffmpeg_s_own_expression()
    {
        // A JPEG header with no frame header in it: the size cannot be read, so ffmpeg is told to fit.
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02 };

        _runner.Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));
        _runner.OnCall = call => File.WriteAllBytes(call.Arguments[^1], JpegOutput);

        await Processor().PrepareAsync(image, 900, CancellationToken.None);

        _runner.Calls.Should().ContainSingle().Which.Arguments.Should()
            .ContainInOrder(
                "-vf",
                "scale='min(iw,900)':'min(ih,900)':force_original_aspect_ratio=decrease:flags=lanczos");
    }

    [Fact]
    public async Task Returns_the_original_bytes_when_ffmpeg_exits_non_zero()
    {
        var image = File.ReadAllBytes(Cover("cover-1600x1600.jpg"));
        string? temporary = null;

        _runner.OnCall = call => temporary = Path.GetDirectoryName(call.Arguments[^1]);
        _runner.Enqueue(1, "Conversion failed!");

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().BeSameAs(image);
        Directory.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_the_original_bytes_when_ffmpeg_times_out()
    {
        var image = File.ReadAllBytes(Cover("cover-1600x1600.jpg"));
        string? temporary = null;

        _runner.OnCall = call => temporary = Path.GetDirectoryName(call.Arguments[^1]);
        _runner.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().BeSameAs(image);
        Directory.Exists(temporary).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_the_original_bytes_when_ffmpeg_is_not_installed()
    {
        var image = File.ReadAllBytes(Cover("cover-1600x1600.jpg"));
        string? temporary = null;

        _runner.OnCall = call => temporary = Path.GetDirectoryName(call.Arguments[^1]);
        _runner.EnqueueMissing(_options.FfmpegPath);

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().BeSameAs(image);
        Directory.Exists(temporary).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Returns_the_original_bytes_when_ffmpeg_writes_nothing_usable(bool writeSomethingElse)
    {
        var image = File.ReadAllBytes(Cover("cover-1600x1600.jpg"));
        string? temporary = null;

        _runner.OnCall = call =>
        {
            temporary = Path.GetDirectoryName(call.Arguments[^1]);

            if (writeSomethingElse)
            {
                File.WriteAllBytes(call.Arguments[^1], [0x00, 0x01, 0x02]);
            }
        };

        _runner.Enqueue(new ProcessResult(0, string.Empty, string.Empty, TimedOut: false));

        var prepared = await Processor().PrepareAsync(image, 1400, CancellationToken.None);

        prepared.Should().BeSameAs(image);
        Directory.Exists(temporary).Should().BeFalse();
    }

    private CoverImageProcessor Processor() =>
        new(_runner, new TestOptionsMonitor<MediaToolsOptions>(_options), NullLogger<CoverImageProcessor>.Instance);
}