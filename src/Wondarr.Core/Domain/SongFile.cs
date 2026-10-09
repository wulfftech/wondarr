using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// The audio file that satisfies a song, if there is one: at most one row per song, so "has a file"
/// is simply "a <c>song_file</c> row exists". Stored in the <c>song_file</c> table.
/// </summary>
public sealed class SongFile : EntityBase
{
    /// <summary>Gets or sets the song this file satisfies.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets the absolute path of the file on disk.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the audio codec, for example <c>flac</c> or <c>mp3</c>.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the container, for example <c>flac</c> or <c>mpeg</c>.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the measured bitrate in kbps, or <see langword="null"/> when unknown.</summary>
    public int? BitrateKbps { get; set; }

    /// <summary>Gets or sets the sample rate in Hz, or <see langword="null"/> when unknown.</summary>
    public int? SampleRate { get; set; }

    /// <summary>Gets or sets the bit depth, or <see langword="null"/> for lossy or unknown audio.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Gets or sets the channel count, or <see langword="null"/> when unknown.</summary>
    public int? Channels { get; set; }

    /// <summary>Gets or sets the measured duration in milliseconds, or <see langword="null"/>.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the quality this file was matched to.</summary>
    public long QualityId { get; set; }

    /// <summary>Gets or sets the AcoustID fingerprint result, or <see langword="null"/> when not fingerprinted.</summary>
    public string? AcoustId { get; set; }

    /// <summary>Gets or sets a value indicating whether the fingerprint confirmed the file's identity.</summary>
    public bool FingerprintVerified { get; set; }

    /// <summary>Gets or sets where the file came from: <c>soulseek</c>, <c>youtube</c>, <c>torrent</c>, <c>usenet</c>, <c>reference</c>.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the source-specific handle (JSON text), or <see langword="null"/>.</summary>
    public string? SourceRef { get; set; }

    /// <summary>Gets or sets the UTC instant the file was imported.</summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>Gets or sets a JSON snapshot of the tags written at import, or <see langword="null"/>.</summary>
    public string? TagsWritten { get; set; }

    /// <summary>
    /// Gets or sets the measured ReplayGain 2.0 track gain in dB (reference -18 LUFS), or
    /// <see langword="null"/> when the file was never measured.
    /// </summary>
    public double? ReplayGainDb { get; set; }

    /// <summary>Gets or sets the measured true peak as a linear value, or <see langword="null"/> when never measured.</summary>
    public double? ReplayGainPeak { get; set; }

    /// <summary>Gets or sets the song this file satisfies.</summary>
    public Song Song { get; set; } = null!;

    /// <summary>Gets or sets the quality this file was matched to.</summary>
    public Quality Quality { get; set; } = null!;
}
