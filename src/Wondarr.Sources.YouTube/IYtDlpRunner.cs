namespace Wondarr.Sources.YouTube;

/// <summary>
/// The yt-dlp half of the YouTube source: what the provider and the health probe call, so a test can
/// stub the process out. The implementation owns the one-at-a-time gate and the pacing flags.
/// </summary>
public interface IYtDlpRunner
{
    /// <summary>Downloads a video's best audio as an Opus remux into <paramref name="destinationDir"/>.</summary>
    /// <param name="videoId">The YouTube Music video id.</param>
    /// <param name="destinationDir">The per-grab folder the queue item names.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    /// <returns>The completed file's path and extension.</returns>
    /// <exception cref="YtDlpException">The run failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled, or yt-dlp exited 101.</exception>
    Task<YtDlpDownload> DownloadAsync(string videoId, string destinationDir, CancellationToken cancellationToken);

    /// <summary>Probes a video's formats (<c>yt-dlp -F</c>): the health probe's check.</summary>
    /// <param name="videoId">The YouTube Music video id.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The probe's stdout and whether a JS runtime was available.</returns>
    /// <exception cref="YtDlpException">The probe failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled, or yt-dlp exited 101.</exception>
    Task<YtDlpFormats> ProbeFormatsAsync(string videoId, CancellationToken cancellationToken);
}
