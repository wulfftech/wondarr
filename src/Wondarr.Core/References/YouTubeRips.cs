using Wondarr.Core.Domain;
using Wondarr.Core.Media;

namespace Wondarr.Core.References;

/// <summary>
/// Spots a reference file that was most likely ripped from YouTube, and rates it no better than a
/// YouTube grab (DECISIONS build session 14 #32). YouTube serves its audio as Opus at 48 kHz (itag 251),
/// while CDs and download stores are 44.1 kHz, so a lossy file at 48 kHz is almost always a rip, whatever
/// it was re-encoded to: yt-dlp's MP3 keeps the 48 kHz. None of the 118 such files in the owner's
/// libraries had a YouTube link left in its tags, so the sample rate is the one sign that survives
/// retagging. A lossless file is never flagged: 48 kHz is a real studio and store rate there.
/// </summary>
public static class YouTubeRips
{
    /// <summary>The sample rate YouTube's audio decodes at, in Hz.</summary>
    public const int SampleRate = 48_000;

    /// <summary>The group rank of OPUS-160, the quality a YouTube grab is rated at (ADR-0008).</summary>
    private static readonly int YouTubeRank = RankOf(SeedData.Opus160QualityId);

    /// <summary>Whether a probed file is most likely a YouTube rip.</summary>
    /// <param name="probe">What ffprobe measured.</param>
    /// <returns><see langword="true"/> for a lossy file at 48 kHz.</returns>
    public static bool IsLikely(MediaInfo probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return !probe.IsLossless && probe.SampleRate == SampleRate;
    }

    /// <summary>
    /// The quality a reference file is rated at: what it measures, except that a likely rip is never
    /// better than OPUS-160. A rip re-encoded to MP3 V0 measures as V0, but it can hold no more than the
    /// Opus stream it came from; one that measures worse keeps its own, lower, quality.
    /// </summary>
    /// <param name="probe">What ffprobe measured.</param>
    /// <returns>The quality id.</returns>
    public static long Rate(MediaInfo probe)
    {
        var measured = MeasuredQuality.FromMediaInfo(probe);

        return IsLikely(probe) && RankOf(measured) > YouTubeRank ? SeedData.Opus160QualityId : measured;
    }

    /// <summary>The seeded group rank of a quality, or 0 for an id the seed does not have.</summary>
    private static int RankOf(long qualityId) =>
        Array.Find(SeedData.Qualities, quality => quality.Id == qualityId)?.Rank ?? 0;
}
