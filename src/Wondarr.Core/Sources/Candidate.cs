using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>Wire names of the source types (<c>source_type</c> columns, blocklist rows, history data).</summary>
public static class SourceTypes
{
    /// <summary>Soulseek through slskd.</summary>
    public const string Soulseek = "soulseek";

    /// <summary>YouTube Music through yt-dlp (Phase 4).</summary>
    public const string YouTube = "youtube";

    /// <summary>Torznab indexers with a torrent client (Phase 7).</summary>
    public const string Torznab = "torznab";

    /// <summary>Newznab indexers with a usenet client (Phase 7).</summary>
    public const string Newznab = "newznab";
}

/// <summary>Whether a candidate is one file or a container (torrent, NZB) holding the file.</summary>
public enum CandidateContainer
{
    /// <summary>The candidate is the audio file itself (Soulseek, YouTube).</summary>
    SingleFile,

    /// <summary>The candidate is a release that contains the file among others.</summary>
    AlbumContainer,
}

/// <summary>
/// What the filename, path or title says about the recording (MATCHING_ENGINE §6.1). Every field is a
/// guess: <see cref="Artist"/> may come from a grandparent folder, <see cref="Album"/> from the parent.
/// </summary>
/// <param name="Artist">Artist guess, as written (not normalised).</param>
/// <param name="Title">Title guess with the track number, extension, version hints and <c>feat.</c> clauses removed (the base title), otherwise as written.</param>
/// <param name="Album">Album guess (usually the parent folder, with a leading year or trailing tags removed).</param>
/// <param name="TrackNo">Track number when the filename carries one.</param>
/// <param name="VersionFlags">Version flags from bracketed or dash-suffixed hints (<see cref="VersionFlagParser"/>).</param>
/// <param name="VersionHints">The raw hint segments that produced <paramref name="VersionFlags"/>, e.g. <c>(Live at Wembley)</c>.</param>
/// <param name="PathVersionFlags">
/// Hard version flags implied by the folders rather than the file name (an album folder "Inni (Live)", a
/// category folder "3. Live"). Weaker evidence than <paramref name="VersionFlags"/>: the decision engine
/// penalises them instead of rejecting.
/// </param>
/// <param name="FeaturedArtists">Artists named in a <c>feat.</c>/<c>ft.</c>/<c>featuring</c> clause.</param>
/// <param name="HasUnexplainedBrackets">
/// Whether the filename (without extension) still contains a <c>[…]</c> or <c>(…)</c> group that is neither a
/// version hint, a featured-artist clause, nor a year — Sockseek's "bracket check".
/// </param>
public sealed record ParsedName(
    string? Artist,
    string? Title,
    string? Album,
    int? TrackNo,
    VersionFlags VersionFlags,
    IReadOnlyList<string> VersionHints,
    VersionFlags PathVersionFlags,
    IReadOnlyList<string> FeaturedArtists,
    bool HasUnexplainedBrackets)
{
    /// <summary>A parse that found nothing.</summary>
    public static ParsedName Empty { get; } = new(null, null, null, null, VersionFlags.None, [], VersionFlags.None, [], false);
}

/// <summary>How easy the candidate is to get right now.</summary>
/// <param name="FreeUploadSlot">Soulseek: the peer has a free upload slot.</param>
/// <param name="QueueLength">Soulseek: the peer's upload queue length.</param>
/// <param name="UploadSpeedBytesPerSecond">Soulseek: the peer's advertised upload speed in bytes per second.</param>
public sealed record CandidateAvailability(
    bool? FreeUploadSlot = null,
    int? QueueLength = null,
    long? UploadSpeedBytesPerSecond = null)
{
    /// <summary>Nothing known.</summary>
    public static CandidateAvailability Unknown { get; } = new();
}

/// <summary>
/// One search result from any source, normalised into the shape the decision engine scores
/// (MATCHING_ENGINE §6.1). Immutable; the decision engine returns its verdict separately.
/// </summary>
public sealed record Candidate
{
    /// <summary>One of <see cref="SourceTypes"/>.</summary>
    public required string SourceType { get; init; }

    /// <summary>The configured source instance, when sources have instances (Phase 4+); null for the bundled slskd.</summary>
    public long? SourceInstanceId { get; init; }

    /// <summary>
    /// Stable identity of this exact file at this source, used for the blocklist and for de-duplication.
    /// Soulseek: <c>{username}\u001f{remote path}</c> (see <see cref="BlocklistKeys.Soulseek"/>).
    /// </summary>
    public required string BlocklistKey { get; init; }

    /// <summary>What the UI shows: the file name, video title or release name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The source's own full path or id for the file, exactly as the source wants it back when grabbing
    /// (Soulseek: the remote filename with its backslashes).
    /// </summary>
    public required string RemotePath { get; init; }

    /// <summary>Soulseek username, YouTube channel or indexer name.</summary>
    public string? Provider { get; init; }

    /// <summary>What the name says.</summary>
    public ParsedName Parsed { get; init; } = ParsedName.Empty;

    /// <summary>Duration in milliseconds when the source reports one.</summary>
    public int? DurationMs { get; init; }

    /// <summary>Lower-case file extension without the dot (<c>flac</c>, <c>mp3</c>, <c>m4a</c>…), when known.</summary>
    public string? Extension { get; init; }

    /// <summary>Audio bitrate in kbps when the source reports one.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Bit depth (lossless only).</summary>
    public int? BitDepth { get; init; }

    /// <summary>Whether the source says the file is variable bitrate.</summary>
    public bool? IsVariableBitrate { get; init; }

    /// <summary>Size in bytes.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Inferred quality id (<c>quality</c> seed, 1 = Unknown). Measured quality replaces it after download.</summary>
    public long QualityId { get; init; } = 1;

    /// <summary>Single file or container.</summary>
    public CandidateContainer Container { get; init; } = CandidateContainer.SingleFile;

    /// <summary>Slots, queue, speed.</summary>
    public CandidateAvailability Availability { get; init; } = CandidateAvailability.Unknown;

    /// <summary>Whether the source reports the file as locked (Soulseek locked files are never grabbed).</summary>
    public bool IsLocked { get; init; }

    /// <summary>The search text that found this candidate (for the record and the UI).</summary>
    public string? Query { get; init; }
}

/// <summary>Builds blocklist keys. One place, so the blocklist and the candidate always agree.</summary>
public static class BlocklistKeys
{
    /// <summary>Unit separator: cannot appear in a Soulseek username or path.</summary>
    public const char Separator = '\u001f';

    /// <summary>Soulseek: the user plus the full remote path, exactly as slskd reports it.</summary>
    public static string Soulseek(string username, string remotePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        return string.Concat(username, Separator.ToString(), remotePath);
    }
}
