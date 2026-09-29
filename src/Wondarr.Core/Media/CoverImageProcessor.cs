using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>
/// Bounds a cover image before it is embedded and written as a folder sidecar, so one album does not
/// carry a multi-megabyte scan through every file (LIBRARY_OUTPUT.md §7.4).
/// </summary>
public interface ICoverImageProcessor
{
    /// <summary>
    /// Returns the image as a JPEG no larger than <paramref name="maxEdge"/> on its longer side.
    /// </summary>
    /// <param name="image">The fetched image, a JPEG or a PNG.</param>
    /// <param name="maxEdge">The longest edge the caller wants, in pixels.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns>
    /// The original bytes when they are already a JPEG within bounds, the converted JPEG, or the
    /// original bytes when the conversion could not be done at all — a cover is never a hard failure.
    /// </returns>
    Task<byte[]> PrepareAsync(byte[] image, int maxEdge, CancellationToken cancellationToken);
}

/// <summary>
/// Bounds cover images with <c>ffmpeg</c>, which is already in the image: the dimensions are read out
/// of the file itself, and only an image that is too large — or is a PNG, which Plexamp embeds less
/// happily — is converted. Nothing here is packaged: a missing tool or a failed conversion returns the
/// image unchanged and says so in the log.
/// </summary>
public sealed partial class CoverImageProcessor : ICoverImageProcessor
{
    /// <summary>The folder under the temporary directory that holds one conversion's files.</summary>
    internal const string TempFolderName = "wondarr-covers";

    /// <summary>How long one conversion may run.</summary>
    internal static readonly TimeSpan ConversionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The quality ffmpeg writes the JPEG at, on its 2–31 scale.</summary>
    private const string JpegQuality = "2";

    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _options;
    private readonly ILogger<CoverImageProcessor> _logger;

    /// <summary>Initialises a new instance of the <see cref="CoverImageProcessor"/> class.</summary>
    /// <param name="runner">Runs ffmpeg without a shell.</param>
    /// <param name="options">The configured ffmpeg path.</param>
    /// <param name="logger">Logs a conversion that could not be done, with the reason and never the image.</param>
    public CoverImageProcessor(
        IProcessRunner runner,
        IOptionsMonitor<MediaToolsOptions> options,
        ILogger<CoverImageProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Reads an image's pixel dimensions from its header, or returns <see langword="null"/> when the
    /// bytes are not a JPEG or a PNG, or the header is truncated. Pure: it never touches the disk.
    /// </summary>
    /// <param name="image">The image bytes.</param>
    /// <returns>The width and height in pixels, or <see langword="null"/>.</returns>
    public static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> image)
    {
        if (IsPng(image))
        {
            // PNG: the signature, the IHDR length and type, then width and height, both 32-bit big-endian.
            if (image.Length < 24)
            {
                return null;
            }

            return (ReadBigEndian32(image[16..]), ReadBigEndian32(image[20..]));
        }

        return IsJpeg(image) ? ReadJpegDimensions(image) : null;
    }

    /// <inheritdoc />
    public async Task<byte[]> PrepareAsync(byte[] image, int maxEdge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdge);

        // A JPEG already within bounds is what we would have produced: hand the same array back and
        // leave the disk alone.
        if (IsJpeg(image) && ReadDimensions(image) is { } size
            && size.Width <= maxEdge && size.Height <= maxEdge)
        {
            return image;
        }

        return await ConvertAsync(image, maxEdge, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Walks a JPEG's marker segments to the frame header, where the size is stored.</summary>
    private static (int Width, int Height)? ReadJpegDimensions(ReadOnlySpan<byte> image)
    {
        var index = 2;

        while (index + 1 < image.Length)
        {
            if (image[index] != 0xFF)
            {
                return null;
            }

            // Fill bytes: any run of FFs may pad a marker.
            while (index < image.Length && image[index] == 0xFF)
            {
                index++;
            }

            if (index >= image.Length)
            {
                return null;
            }

            var marker = image[index];
            index++;

            // Standalone markers carry no length: TEM, a second SOI and the restart markers.
            if (marker == 0x01 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            // EOI, or a stuffed byte where a marker should be: there is no frame header to find.
            if (marker == 0xD9 || marker == 0x00)
            {
                return null;
            }

            if (index + 1 >= image.Length)
            {
                return null;
            }

            var length = ReadBigEndian16(image[index..]);

            if (length < 2)
            {
                return null;
            }

            if (IsFrameHeader(marker))
            {
                // FF xx len(2) precision(1) height(2) width(2): the offsets count from the FF.
                if (index + 6 >= image.Length)
                {
                    return null;
                }

                return (ReadBigEndian16(image[(index + 5)..]), ReadBigEndian16(image[(index + 3)..]));
            }

            index += length;
        }

        return null;
    }

    /// <summary>
    /// Whether a marker introduces a frame: C0–CF minus the three that share the range but are not
    /// frames (DHT, JPG and DAC).
    /// </summary>
    private static bool IsFrameHeader(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    private async Task<byte[]> ConvertAsync(byte[] image, int maxEdge, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            TempFolderName,
            Guid.NewGuid().ToString("N"));

        var input = Path.Combine(directory, IsPng(image) ? "in.png" : "in.jpg");
        var output = Path.Combine(directory, "out.jpg");

        try
        {
            Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(input, image, cancellationToken).ConfigureAwait(false);

            var result = await _runner
                .RunAsync(
                    _options.CurrentValue.FfmpegPath,
                    [
                        "-hide_banner",
                        "-loglevel", "error",
                        "-nostdin",
                        "-y",
                        "-i", input,
                        "-vf", Scale(image, maxEdge),
                        "-frames:v", "1",
                        "-q:v", JpegQuality,
                        output,
                    ],
                    ConversionTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.TimedOut)
            {
                return Failed(image, "ffmpeg timed out");
            }

            if (result.ExitCode != 0)
            {
                return Failed(
                    image,
                    MediaProbe.FirstErrorLine(result.StandardError) ?? $"ffmpeg exited {result.ExitCode}");
            }

            if (!File.Exists(output))
            {
                return Failed(image, "ffmpeg wrote no output");
            }

            var converted = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);

            if (converted.Length == 0)
            {
                return Failed(image, "ffmpeg wrote an empty output");
            }

            return IsJpeg(converted)
                ? converted
                : Failed(image, "ffmpeg wrote something that is not a JPEG");
        }
        catch (MediaToolMissingException exception)
        {
            return Failed(image, exception.Message);
        }
        catch (IOException exception)
        {
            return Failed(image, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed(image, exception.Message);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// The <c>scale</c> filter: an exact box that keeps the aspect ratio when the size is known, and
    /// an expression ffmpeg evaluates itself when it is not.
    /// </summary>
    private static string Scale(byte[] image, int maxEdge)
    {
        if (ReadDimensions(image) is not { } size || size.Width <= 0 || size.Height <= 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"scale='min(iw,{maxEdge})':'min(ih,{maxEdge})':force_original_aspect_ratio=decrease:flags=lanczos");
        }

        var longer = Math.Max(size.Width, size.Height);
        var target = Math.Min(maxEdge, longer);
        var width = size.Width * target / longer;
        var height = size.Height * target / longer;

        return width < 1 || height < 1
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"scale='min(iw,{maxEdge})':'min(ih,{maxEdge})':force_original_aspect_ratio=decrease:flags=lanczos")
            : string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=lanczos");
    }

    /// <summary>Logs why the image could not be bounded and answers with the image itself.</summary>
    private byte[] Failed(byte[] original, string reason)
    {
        LogNotResized(_logger, reason);

        return original;
    }

    private static int ReadBigEndian16(ReadOnlySpan<byte> bytes) => (bytes[0] << 8) | bytes[1];

    private static int ReadBigEndian32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static bool IsJpeg(ReadOnlySpan<byte> image) => StartsWith(image, JpegMagic);

    private static bool IsPng(ReadOnlySpan<byte> image) => StartsWith(image, PngMagic);

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary folder is not worth failing an import over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Warning,
        Message = "The cover was embedded unbounded: {Reason}")]
    private static partial void LogNotResized(ILogger logger, string reason);
}