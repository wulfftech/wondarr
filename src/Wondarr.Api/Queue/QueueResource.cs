using Wondarr.Core.Domain;

namespace Wondarr.Api.Queue;

/// <summary>
/// One row of <c>/api/v1/queue</c> in Lidarr's shape: what is downloading, how far it has got and
/// which candidate it is carrying. The candidate's own columns are joined in by the query that
/// produced the item, so the mapper never triggers a second round trip.
/// </summary>
/// <param name="Id">The queue item id.</param>
/// <param name="SongId">The song the grab is for.</param>
/// <param name="SongTitle">The song's title.</param>
/// <param name="ArtistCredit">The song's display credit.</param>
/// <param name="SourceType">One of <c>soulseek</c>, <c>youtube</c>, <c>torznab</c> or <c>newznab</c>.</param>
/// <param name="Provider">The Soulseek username, YouTube channel or indexer, or <see langword="null"/>.</param>
/// <param name="DisplayName">What the UI shows for the file being downloaded.</param>
/// <param name="RemotePath">The source's own path or id for the file.</param>
/// <param name="State">Where the grab is in the pipeline, as a camel-case string.</param>
/// <param name="Progress">How far the download has got, 0–1.</param>
/// <param name="BytesTransferred">How many bytes have arrived.</param>
/// <param name="SizeBytes">The total size when the source reported one, or <see langword="null"/>.</param>
/// <param name="PlaceInQueue">The position in the source's own queue (Soulseek), or <see langword="null"/>.</param>
/// <param name="Message">The last message the source reported, or <see langword="null"/>.</param>
/// <param name="Attempt">Which automatic attempt of the search this grab is, 1-based.</param>
/// <param name="QualityId">The candidate's inferred quality.</param>
/// <param name="QualityName">The quality's display name, or <see langword="null"/> when the id is unknown.</param>
/// <param name="CreatedAt">The UTC instant the grab was recorded.</param>
/// <param name="StateChangedAt">The UTC instant the state last changed.</param>
/// <param name="FinishedAt">The UTC instant the grab finished, or <see langword="null"/> while it is in flight.</param>
public sealed record QueueResource(
    long Id,
    long SongId,
    string SongTitle,
    string ArtistCredit,
    string SourceType,
    string? Provider,
    string DisplayName,
    string RemotePath,
    QueueItemState State,
    double Progress,
    long BytesTransferred,
    long? SizeBytes,
    int? PlaceInQueue,
    string? Message,
    int Attempt,
    long QualityId,
    string? QualityName,
    DateTime CreatedAt,
    DateTime StateChangedAt,
    DateTime? FinishedAt);

/// <summary>Maps a stored queue item onto the wire.</summary>
public static class QueueResourceExtensions
{
    /// <summary>The quality id the ladder falls back to when a candidate carries none (Unknown).</summary>
    private const long UnknownQualityId = 1;

    /// <summary>
    /// Shapes one queue item. <paramref name="qualityNames"/> is the id-to-name lookup the caller read
    /// once per request, because the quality ladder is seed data every row shares.
    /// </summary>
    /// <param name="item">The item, with its song and candidate loaded.</param>
    /// <param name="qualityNames">Quality id → display name.</param>
    public static QueueResource ToResource(this QueueItem item, IReadOnlyDictionary<long, string> qualityNames)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(qualityNames);

        var candidate = item.Candidate;
        var qualityId = candidate?.QualityId ?? UnknownQualityId;

        return new QueueResource(
            item.Id,
            item.SongId,
            item.Song?.Title ?? string.Empty,
            item.Song?.ArtistCredit ?? string.Empty,
            item.SourceType,
            candidate?.Provider,
            candidate?.DisplayName ?? string.Empty,
            candidate?.RemotePath ?? string.Empty,
            item.State,
            item.Progress,
            item.BytesTransferred,
            item.SizeBytes,
            item.PlaceInQueue,
            item.Message,
            item.Attempt,
            qualityId,
            qualityNames.TryGetValue(qualityId, out var name) ? name : null,
            item.CreatedAt,
            item.StateChangedAt,
            item.FinishedAt);
    }
}
