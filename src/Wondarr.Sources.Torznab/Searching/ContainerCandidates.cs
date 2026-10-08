using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// A release and its files (null while unknown), with the info-hash the metainfo gave and the
/// container's whole size when the listing showed it.
/// </summary>
/// <param name="Release">The release as the indexer (or a push) described it.</param>
/// <param name="Files">The container's files, or null while they are unknown.</param>
/// <param name="InfoHash">The torrent's info-hash, when known.</param>
/// <param name="TotalSize">The whole container's size, when the listing showed it.</param>
public sealed record ContainerListing(
    IndexerRelease Release,
    IReadOnlyList<ContainerFile>? Files,
    string? InfoHash,
    long? TotalSize);

/// <summary>
/// Turns a container into the wanted song's candidate (DECISIONS build session 8 #3): the song's file
/// found by the matcher, or the release itself while its file list is unknown. Shared by the indexer
/// search and <c>release/push</c>.
/// </summary>
public static class ContainerCandidates
{
    /// <summary>The wanted song's candidate from one container, or null when its file is not in it.</summary>
    /// <param name="protocol">Torrents or usenet.</param>
    /// <param name="listing">The release and its file list, when it was read.</param>
    /// <param name="wanted">The song; its quality is replaced by the release name's claim.</param>
    /// <param name="query">The search text that found the release, for the record.</param>
    /// <param name="now">The instant a post's age is measured at.</param>
    public static Candidate? Build(
        DownloadProtocol protocol,
        ContainerListing listing,
        ContainerMatchRequest wanted,
        string query,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(wanted);

        var release = listing.Release;
        var sourceType = protocol == DownloadProtocol.Torrent ? SourceTypes.Torznab : SourceTypes.Newznab;
        var claimed = ReleaseQualityParser.Parse(release.Title);
        var parsedRelease = ReleaseTitleParser.Parse(release.Title);
        var releaseFlags = VersionFlagParser.Parse(release.Title).Flags;
        var song = wanted with { QualityId = claimed };

        if (listing.Files is not { } files)
        {
            // No file list before the grab: the release itself is the candidate, matched once the
            // client has the metadata or the job is unpacked (P7-07). Its path is the release title.
            return new Candidate
            {
                SourceType = sourceType,
                SourceInstanceId = release.IndexerId,
                BlocklistKey = BlocklistKey(protocol, release, listing.InfoHash, release.Title),
                DisplayName = release.Title,
                RemotePath = release.Title,
                Provider = release.IndexerName,
                Parsed = ParsedName.Empty with
                {
                    Artist = parsedRelease?.Artist,
                    Album = parsedRelease?.Album,
                    PathVersionFlags = releaseFlags,
                },
                QualityId = claimed,
                Container = CandidateContainer.AlbumContainer,
                Availability = Availability(release, fileListKnown: false, now),
                Query = query,
                Release = Release(release, listing, null, song),
            };
        }

        var match = ContainerMatcher.Find(files, song);

        if (match is null)
        {
            return null;
        }

        var path = match.File.Path;

        return new Candidate
        {
            SourceType = sourceType,
            SourceInstanceId = release.IndexerId,
            BlocklistKey = BlocklistKey(protocol, release, listing.InfoHash, path),
            DisplayName = Path.GetFileName(path),
            RemotePath = path,
            Provider = release.IndexerName,
            Parsed = match.Parsed with
            {
                // A file named "07 - Title.flac" in "Discovery [FLAC]" names no artist; the release does.
                Artist = string.IsNullOrWhiteSpace(match.Parsed.Artist) ? parsedRelease?.Artist ?? release.Title : match.Parsed.Artist,
                Album = string.IsNullOrWhiteSpace(match.Parsed.Album) ? parsedRelease?.Album : match.Parsed.Album,
            },
            Extension = match.Extension,
            SizeBytes = match.File.Size,
            QualityId = FileQuality(claimed, match, wanted.DurationMs),
            Container = CandidateContainer.AlbumContainer,
            Availability = Availability(release, fileListKnown: true, now),
            Query = query,
            Release = Release(release, listing, match.File.Index, song),
        };
    }

    /// <summary>
    /// The release name's quality, unless the file's own extension says another codec: a "FLAC" release
    /// whose matched file is an <c>.mp3</c> is an MP3, never lossless.
    /// </summary>
    private static long FileQuality(long claimed, ContainerMatch match, int? durationMs)
    {
        var inferred = SoulseekQuality.Infer(
            match.Extension,
            bitRateKbps: null,
            isVariableBitRate: null,
            sampleRate: null,
            bitDepth: null,
            lengthSeconds: durationMs / 1000,
            sizeBytes: match.File.Size);

        if (claimed == 1)
        {
            return inferred;
        }

        if (inferred == 1)
        {
            return claimed;
        }

        var claimedCodec = SeedData.Qualities.FirstOrDefault(quality => quality.Id == claimed)?.Codec;
        var inferredCodec = SeedData.Qualities.FirstOrDefault(quality => quality.Id == inferred)?.Codec;

        return string.Equals(claimedCodec, inferredCodec, StringComparison.Ordinal) ? claimed : inferred;
    }

    private static CandidateAvailability Availability(IndexerRelease release, bool fileListKnown, DateTimeOffset now)
    {
        int? ageDays = release.PublishDate is { } published
            ? Math.Max(0, (int)(now - published).TotalDays)
            : null;

        return new CandidateAvailability(
            Seeders: release.Seeders,
            Grabs: release.Grabs,
            AgeDays: ageDays,
            Freeleech: release.DownloadVolumeFactor is 0,
            FileListKnown: fileListKnown);
    }

    private static ContainerRelease Release(
        IndexerRelease release,
        ContainerListing listing,
        int? fileIndex,
        ContainerMatchRequest song) => new()
        {
            IndexerId = release.IndexerId,
            IndexerName = release.IndexerName,
            Title = release.Title,
            ReleaseId = release.ReleaseId,
            DownloadUrl = release.DownloadUrl,
            MagnetUrl = release.MagnetUrl,
            InfoHash = listing.InfoHash,
            Size = release.Size ?? listing.TotalSize,
            PublishDate = release.PublishDate,
            FileIndex = fileIndex,
            Files = listing.Files,
            Song = song,
        };

    /// <summary>
    /// <c>{infohash}␟{path}</c> for a torrent (the guid stands in while the hash is unknown),
    /// <c>{guid}␟{name}</c> for a usenet post.
    /// </summary>
    private static string BlocklistKey(DownloadProtocol protocol, IndexerRelease release, string? infoHash, string path) =>
        protocol == DownloadProtocol.Torrent
            ? BlocklistKeys.Torrent(string.IsNullOrEmpty(infoHash) ? release.ReleaseId : infoHash, path)
            : BlocklistKeys.Usenet(release.ReleaseId, path);
}
