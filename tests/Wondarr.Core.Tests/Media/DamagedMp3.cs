using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// A fact that only runs where ffmpeg and ffprobe (and, with <c>fpcalc</c> set, fpcalc) are on PATH: it
/// builds a damaged MP3 and decodes it with the real tools. The dev machines have no ffmpeg; these run
/// inside the image and on any box with the tools installed.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="FfmpegFactAttribute"/> class.</summary>
    /// <param name="fpcalc">Whether the test also needs fpcalc.</param>
    public FfmpegFactAttribute(bool fpcalc = false)
    {
        if (!DamagedMp3.ToolsAvailable(fpcalc))
        {
            Skip = fpcalc ? "ffmpeg, ffprobe and fpcalc are not on PATH" : "ffmpeg and ffprobe are not on PATH";
        }
    }
}

/// <summary>A theory that only runs where the real tools are on PATH, like <see cref="FfmpegFactAttribute"/>.</summary>
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    /// <summary>Initialises a new instance of the <see cref="FfmpegTheoryAttribute"/> class.</summary>
    /// <param name="fpcalc">Whether the test also needs fpcalc.</param>
    public FfmpegTheoryAttribute(bool fpcalc = false)
    {
        if (!DamagedMp3.ToolsAvailable(fpcalc))
        {
            Skip = fpcalc ? "ffmpeg, ffprobe and fpcalc are not on PATH" : "ffmpeg and ffprobe are not on PATH";
        }
    }
}

/// <summary>
/// Builds the damaged MP3s of the reference-scan tolerance tests: a 30 s tone encoded by the real ffmpeg
/// (once per run), with the body or the whole of chosen frames overwritten with noise. Which bytes are
/// written is fixed (a seeded generator), so a given damage is the same on every run.
/// </summary>
internal static class DamagedMp3
{
    private static readonly Lazy<Task<byte[]>> Clean = new(EncodeAsync);

    /// <summary>Whether the real tools the live tests need are installed.</summary>
    /// <param name="fpcalc">Whether fpcalc is needed too.</param>
    public static bool ToolsAvailable(bool fpcalc = false) =>
        MediaToolsFactAttribute.OnPath("ffmpeg")
        && MediaToolsFactAttribute.OnPath("ffprobe")
        && (!fpcalc || MediaToolsFactAttribute.OnPath("fpcalc"));

    /// <summary>A real fingerprinter over the real tools on PATH.</summary>
    public static Fingerprinter RealFingerprinter() =>
        new(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<Fingerprinter>.Instance);

    /// <summary>A real probe, the way the app builds it, over the real tools on PATH.</summary>
    public static MediaProbe RealProbe() =>
        new(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<MediaProbe>.Instance);

    /// <summary>
    /// A 30 s MP3 with some of its frames overwritten.
    /// </summary>
    /// <param name="wholeFrames">
    /// <c>true</c> overwrites the header too (the demuxer loses the frame and the decoder says
    /// "Header missing"); <c>false</c> keeps the header and overwrites the body (the decoder finds
    /// "invalid" data in a frame it can place).
    /// </param>
    /// <param name="frames">Which frames, counted from the first audio frame (the Xing/Info frame is 0).</param>
    public static async Task<byte[]> CreateAsync(bool wholeFrames, params int[] frames)
    {
        var data = (byte[])(await Clean.Value.ConfigureAwait(false)).Clone();
        var layout = Frames(data);
        var noise = new Random(7);

        foreach (var index in frames)
        {
            var (offset, size) = layout[index];
            var start = wholeFrames ? offset : offset + 4;

            noise.NextBytes(data.AsSpan(start, offset + size - start));
        }

        return data;
    }

    /// <summary>The file cut in half: the header's frame count and duration still describe all of it.</summary>
    /// <param name="data">The whole file.</param>
    public static byte[] Truncate(byte[] data) => data[..(data.Length / 2)];

    /// <summary>Writes a file into a folder and returns its path.</summary>
    /// <param name="directory">The folder, which is created when it is not there.</param>
    /// <param name="name">The file name, or a path below the folder (its folders are created too).</param>
    /// <param name="bytes">The file's content.</param>
    public static string Write(string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private static async Task<byte[]> EncodeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wondarr-tone-{Guid.NewGuid():N}.mp3");

        try
        {
            var result = await new ProcessRunner(NullLogger<ProcessRunner>.Instance)
                .RunAsync(
                    "ffmpeg",
                    [
                        "-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=30",
                        "-ac", "2", "-b:a", "128k", "-y", path,
                    ],
                    TimeSpan.FromSeconds(60),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg could not encode the test tone: {result.StandardError}");
            }

            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The offset and size of every MPEG-1 Layer III frame, found from the frame headers.</summary>
    private static List<(int Offset, int Size)> Frames(byte[] data)
    {
        int[] bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] sampleRates = [44100, 48000, 32000];

        var frames = new List<(int Offset, int Size)>();
        var position = 0;

        while (position + 4 <= data.Length)
        {
            // 0xFFFB / 0xFFFA: sync, MPEG-1, Layer III. The ID3 tag in front has neither.
            if (data[position] == 0xFF && (data[position + 1] & 0xFE) == 0xFA
                && data[position + 2] >> 4 is > 0 and < 15 && ((data[position + 2] >> 2) & 3) < 3)
            {
                var bitrate = bitrates[data[position + 2] >> 4];
                var sampleRate = sampleRates[(data[position + 2] >> 2) & 3];
                var size = (144 * bitrate * 1000 / sampleRate) + ((data[position + 2] >> 1) & 1);

                frames.Add((position, size));
                position += size;
            }
            else
            {
                position++;
            }
        }

        return frames;
    }
}
