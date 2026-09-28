using Wondarr.Core.Sources;

namespace Wondarr.Core.Media;

/// <summary>
/// What ffprobe measured about one file (MATCHING_ENGINE.md §6.5 step 1). Everything is what the
/// audio stream and the container actually say, not what the file name claims.
/// </summary>
/// <param name="Codec">The ffprobe codec name, for example <c>flac</c> or <c>pcm_s16le</c>.</param>
/// <param name="Container">The first format name ffprobe reports, for example <c>mp3</c> or <c>mov</c>.</param>
/// <param name="BitrateKbps">The overall bitrate in kbps, rounded; <c>null</c> when ffprobe reports none.</param>
/// <param name="SampleRate">The sample rate in Hz, or <c>null</c>.</param>
/// <param name="BitDepth">The bit depth, for lossless codecs only, or <c>null</c>.</param>
/// <param name="Channels">The channel count, or <c>null</c>.</param>
/// <param name="DurationMs">The duration in milliseconds, rounded.</param>
/// <param name="IsLossless">Whether the codec stores the samples losslessly.</param>
/// <param name="SizeBytes">The file size in bytes, or <c>0</c> when ffprobe reports none.</param>
public sealed record MediaInfo(
    string Codec,
    string Container,
    int? BitrateKbps,
    int? SampleRate,
    int? BitDepth,
    int? Channels,
    int DurationMs,
    bool IsLossless,
    long SizeBytes);

/// <summary>The codecs Wondarr treats as lossless, and therefore as carrying a bit depth.</summary>
internal static class MediaCodecs
{
    /// <summary>Whether a codec name identifies a lossless format.</summary>
    /// <param name="codec">The ffprobe codec name.</param>
    public static bool IsLossless(string codec) =>
        codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("alac", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("ape", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("wavpack", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extension <see cref="SoulseekQuality.Infer"/> should reason about for a codec.</summary>
    /// <param name="codec">The ffprobe codec name.</param>
    /// <param name="container">The container name, which separates WAV from AIFF for raw PCM.</param>
    /// <returns>The extension, or <c>null</c> when the format is one the rules know nothing about.</returns>
    public static string? Extension(string codec, string container)
    {
        if (codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
        {
            return container.Contains("aiff", StringComparison.OrdinalIgnoreCase) ? "aiff" : "wav";
        }

        return codec.ToLowerInvariant() switch
        {
            "mp3" => "mp3",
            "flac" => "flac",
            "aac" => "m4a",
            "alac" => "alac",
            "opus" => "opus",
            "vorbis" => "ogg",
            "ape" => "ape",
            "wavpack" => "wv",
            "wmav1" or "wmav2" => "wma",
            _ => null,
        };
    }
}

/// <summary>
/// The quality a downloaded file actually is, measured rather than inferred (MATCHING_ENGINE.md §6.5
/// step 1). It runs the same rules the Soulseek results do, so a measured FLAC and a claimed FLAC sort
/// into the same tier of a quality profile.
/// </summary>
public static class MeasuredQuality
{
    /// <summary>Reads the quality id of a probed file from what ffprobe measured.</summary>
    /// <param name="info">The probe result.</param>
    /// <returns>The quality id (<c>quality</c> seed, <c>1</c> = Unknown).</returns>
    public static long FromMediaInfo(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var extension = MediaCodecs.Extension(info.Codec, info.Container);
        if (extension is null)
        {
            return SoulseekQuality.Unknown;
        }

        // ffprobe cannot say whether a bitrate is variable, so the rules decide from the measured
        // bitrate: a value that is not a standard CBR tier is variable bitrate.
        return SoulseekQuality.Infer(
            extension,
            info.BitrateKbps,
            isVariableBitRate: null,
            sampleRate: info.SampleRate,
            bitDepth: info.BitDepth,
            lengthSeconds: info.DurationMs / 1000,
            sizeBytes: info.SizeBytes > 0 ? info.SizeBytes : null);
    }
}