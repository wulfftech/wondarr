namespace Wondarr.Core.Searching;

/// <summary>A release pushed to <c>/api/v1/release/push</c>, in Lidarr's terms (autobrr sends it).</summary>
/// <param name="Title">The release name.</param>
/// <param name="Protocol"><c>torrent</c> or <c>usenet</c>.</param>
/// <param name="DownloadUrl">The <c>.torrent</c> or NZB link; can carry a passkey, so it is never logged.</param>
/// <param name="MagnetUrl">The magnet link.</param>
/// <param name="Size">The release's size in bytes.</param>
/// <param name="Indexer">The indexer's name, as the pusher knows it.</param>
/// <param name="PublishDate">When the release was published.</param>
public sealed record PushedRelease(
    string Title,
    string Protocol,
    string? DownloadUrl,
    string? MagnetUrl,
    long? Size,
    string? Indexer,
    DateTimeOffset? PublishDate);

/// <summary>What became of a pushed release.</summary>
/// <param name="Approved">Whether it was grabbed for at least one wanted song.</param>
/// <param name="Rejections">Why not, one sentence per reason; empty when approved.</param>
/// <param name="SongId">The first wanted song it matched, when any.</param>
/// <param name="SongTitle">That song's title.</param>
public sealed record ReleasePushOutcome(bool Approved, IReadOnlyList<string> Rejections, long? SongId, string? SongTitle);

/// <summary>
/// Decides a pushed release (DECISIONS build session 8 #11): matched against the wanted songs by its
/// artist and album, its file list read, each matching song judged by the engine, and grabbed when
/// approved. Implemented by the torrent and usenet sources.
/// </summary>
public interface IReleasePushHandler
{
    /// <summary>Decides, and grabs when approved.</summary>
    /// <param name="release">The pushed release.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    Task<ReleasePushOutcome> PushAsync(PushedRelease release, CancellationToken cancellationToken);
}
