using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Turns the results of one InnerTube search into <see cref="Candidate"/>s. Pure code: nothing here
/// scores or rejects anything — the video type stays on the parsed result, and the engine decides.
/// </summary>
public static class YouTubeCandidateMapper
{
    /// <summary>
    /// The quality table's OPUS-160 row: a YouTube Music stream is Opus at roughly 130 kbps, so the
    /// mid-lossy bucket is the honest guess until the download itself is measured (P2-04).
    /// </summary>
    public const long Opus160QualityId = 28;

    /// <summary>The blocklist key prefix of a YouTube candidate: the video id is the stable identity.</summary>
    private const string BlocklistKeyPrefix = "youtube:";

    /// <summary>Maps the results of one search, dropping the repeats (the card and a shelf item can be the same video).</summary>
    /// <param name="results">The results, card first.</param>
    /// <param name="query">The search text that found them, recorded on every candidate.</param>
    public static IReadOnlyList<Candidate> Map(IEnumerable<InnertubeResult> results, string query)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            var key = BlocklistKey(result.VideoId);

            if (!seen.Add(key))
            {
                continue;
            }

            candidates.Add(Map(result, key, query));
        }

        return candidates;
    }

    /// <summary>Maps one result.</summary>
    public static Candidate Map(InnertubeResult result, string query) => Map(result, BlocklistKey(result.VideoId), query);

    /// <summary>The blocklist key of a YouTube candidate: the video id is the stable identity.</summary>
    public static string BlocklistKey(string videoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        return string.Concat(BlocklistKeyPrefix, videoId);
    }

    private static Candidate Map(InnertubeResult result, string key, string query)
    {
        // A YouTube title carries the same version hints and feat. clauses a file name does, so the
        // same parser strips them out of the base title the engine matches on.
        var version = VersionFlagParser.Parse(result.Title);
        var artist = result.Artists.Count > 0 ? result.Artists[0] : null;

        return new Candidate
        {
            SourceType = SourceTypes.YouTube,
            BlocklistKey = key,
            DisplayName = result.Title,
            RemotePath = result.VideoId,
            Provider = artist,
            Parsed = new ParsedName(
                artist,
                version.BaseTitle,
                result.Album,
                TrackNo: null,
                version.Flags,
                version.Hints,
                PathVersionFlags: VersionFlags.None,
                FeaturedArtists: [],
                HasUnexplainedBrackets: false),
            DurationMs = result.DurationMs,
            // Unknown until yt-dlp has the stream: the container and the bitrate are decided then.
            Extension = null,
            QualityId = Opus160QualityId,
            // The engine's YouTube rule is a flat 120 availability (MATCHING_ENGINE.md §6.2): a free
            // slot (80) and an empty queue (40). Nothing else about a stream is known up front.
            Availability = new CandidateAvailability(FreeUploadSlot: true, QueueLength: 0),
            Query = query,
        };
    }
}
