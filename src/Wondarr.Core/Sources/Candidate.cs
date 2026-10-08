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

    /// <summary>
    /// A file the user already had, found by a reference-library scan (Phase 3). Wondarr never
    /// searches for, replaces or writes such a file: it only records that the song is owned.
    /// </summary>
    public const string Reference = "reference";

    /// <summary>
    /// A copy of a reference-library file that was adopted into a managed library (Phase 3): from the
    /// adoption on it is an ordinary library file, written, named and placed by the organizer. The
    /// user's original stays exactly where it was.
    /// </summary>
    public const string Adopted = "adopted";
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
/// <param name="Seeders">Torrents: the indexer's seeder count.</param>
/// <param name="Grabs">Usenet: how often the post was grabbed, when the indexer says.</param>
/// <param name="AgeDays">Usenet: the post's age in days.</param>
/// <param name="Freeleech">Torrents: the download does not count against the user's ratio.</param>
/// <param name="FileListKnown">Torrents and usenet: the container's file list was read before the grab.</param>
public sealed record CandidateAvailability(
    bool? FreeUploadSlot = null,
    int? QueueLength = null,
    long? UploadSpeedBytesPerSecond = null,
    int? Seeders = null,
    int? Grabs = null,
    int? AgeDays = null,
    bool Freeleech = false,
    bool FileListKnown = false)
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

    /// <summary>
    /// Torrents and usenet: the release the file is inside, and how to fetch it. <see cref="RemotePath"/>
    /// is the file's path inside the container (the release title while the file list is unknown).
    /// </summary>
    public ContainerRelease? Release { get; init; }
}

/// <summary>
/// The release (torrent or NZB) a container candidate comes from: what the indexer said about it, and
/// the wanted file's place inside it (DECISIONS build session 8 #3).
/// </summary>
public sealed record ContainerRelease
{
    /// <summary>The indexer row that listed the release.</summary>
    public required long IndexerId { get; init; }

    /// <summary>The indexer's name, for the UI and history.</summary>
    public required string IndexerName { get; init; }

    /// <summary>The release name as the indexer lists it.</summary>
    public required string Title { get; init; }

    /// <summary>The indexer's id for the release, its <c>guid</c> (a usenet blocklist key).</summary>
    public required string ReleaseId { get; init; }

    /// <summary>The <c>.torrent</c> or NZB link, when there is one.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>The magnet link, when there is one.</summary>
    public string? MagnetUrl { get; init; }

    /// <summary>Torrents: the info-hash, lower-case hex, when known.</summary>
    public string? InfoHash { get; init; }

    /// <summary>The whole release's size in bytes.</summary>
    public long? Size { get; init; }

    /// <summary>The release's publish date.</summary>
    public DateTimeOffset? PublishDate { get; init; }

    /// <summary>The wanted file's index in <see cref="Files"/>; null while the file list is unknown.</summary>
    public int? FileIndex { get; init; }

    /// <summary>The container's files, when they were read before the grab (bundling looks here).</summary>
    public IReadOnlyList<ContainerFile>? Files { get; init; }

    /// <summary>
    /// The paths of the other wanted songs' files the same grab fetches (bundling, DECISIONS build
    /// session 8 #6): set by the search service on the first song's grab only, so the client selects
    /// them — and SABnzbd keeps them — from the start.
    /// </summary>
    public IReadOnlyList<string>? AlsoWanted { get; init; }

    /// <summary>
    /// The song this candidate is for, as the container matcher reads it: how the file is found once
    /// a magnet's metadata or an obfuscated post's unpacked files are there.
    /// </summary>
    public ContainerMatchRequest? Song { get; init; }
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

    /// <summary>A file inside a torrent: the info-hash and the file's path in it.</summary>
    /// <param name="infoHash">The torrent's info-hash (lower-cased here).</param>
    /// <param name="path">The file's path inside the torrent, or the release title while unknown.</param>
    public static string Torrent(string infoHash, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(infoHash);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return string.Concat(infoHash.ToLowerInvariant(), Separator.ToString(), path);
    }

    /// <summary>A file inside an NZB: the indexer's guid for the post and the file's name.</summary>
    /// <param name="releaseId">The indexer's guid for the post.</param>
    /// <param name="path">The file's name, or the release title while unknown.</param>
    public static string Usenet(string releaseId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return string.Concat(releaseId, Separator.ToString(), path);
    }
}
