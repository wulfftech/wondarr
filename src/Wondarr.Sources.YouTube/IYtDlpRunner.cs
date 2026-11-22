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
    /// <exception cref="YtDlpException">The run failed (an exit code of 101 included); the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<YtDlpDownload> DownloadAsync(string videoId, string destinationDir, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a music video: the best video and audio up to <paramref name="maxHeight"/>, merged into one MKV
    /// in <paramref name="destinationDir"/>, with its thumbnail converted to JPEG beside it. It takes the same
    /// one-at-a-time gate and the same cookies and PO token as <see cref="DownloadAsync"/>.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="destinationDir">The folder the files are written to.</param>
    /// <param name="maxHeight">The tallest picture to take, in pixels, or <see langword="null"/> for no cap.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    /// <param name="started">Called once the gate is held and the run is about to begin, or <see langword="null"/>.</param>
    /// <returns>The MKV's path, where its thumbnail is, and the length yt-dlp reported.</returns>
    /// <exception cref="YtDlpException">The run failed, or the file is not an MKV; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<YtDlpVideoDownload> DownloadVideoAsync(
        string videoId,
        string destinationDir,
        int? maxHeight,
        CancellationToken cancellationToken,
        Func<Task>? started = null);

    /// <summary>
    /// Asks yt-dlp for the height of the picture it would download under the same format rule as
    /// <see cref="DownloadVideoAsync"/> (<c>--skip-download --print height</c>): nothing is downloaded. It takes the same
    /// one-at-a-time gate.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="maxHeight">The tallest picture to take, in pixels, or <see langword="null"/> for no cap.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    /// <returns>The height in pixels, or <see langword="null"/> when yt-dlp printed none.</returns>
    /// <exception cref="YtDlpException">The run failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<int?> ReadBestHeightAsync(string videoId, int? maxHeight, CancellationToken cancellationToken);

    /// <summary>Probes a video's formats (<c>yt-dlp -F</c>): the health probe's check.</summary>
    /// <param name="videoId">The YouTube Music video id.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The probe's stdout and whether a JS runtime was available.</returns>
    /// <exception cref="YtDlpException">The probe failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<YtDlpFormats> ProbeFormatsAsync(string videoId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one video's details (<c>yt-dlp -J</c>, nothing downloaded): the title, channel, upload date
    /// and length the Match queue's "Add to MusicBrainz" form fills in.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    /// <returns>What yt-dlp read; a field YouTube did not give is <see langword="null"/>.</returns>
    /// <exception cref="YtDlpException">
    /// The read failed, or a download held yt-dlp for longer than a person should wait; the kind carries the classification.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<YtDlpVideo> ReadVideoAsync(string videoId, CancellationToken cancellationToken);
}
