using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Downloads a music video with <see cref="IYtDlpRunner.DownloadVideoAsync"/> into a folder of its own under
/// <c>youtube.downloads_dir</c>, and removes that folder when the importer is done with it. The folder's name is a
/// fresh GUID, so two downloads never share files.
/// </summary>
public sealed partial class YtDlpMusicVideoDownloader : IMusicVideoDownloader
{
    /// <summary>The folder, under the YouTube downloads folder, the work folders are made in.</summary>
    internal const string WorkRootName = "music-videos";

    private readonly IYtDlpRunner _runner;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly ILogger<YtDlpMusicVideoDownloader> _logger;

    /// <summary>Initialises a new instance of the <see cref="YtDlpMusicVideoDownloader"/> class.</summary>
    /// <param name="runner">Runs yt-dlp.</param>
    /// <param name="options">The <c>youtube</c> section, for the downloads folder.</param>
    /// <param name="logger">The logger.</param>
    public YtDlpMusicVideoDownloader(
        IYtDlpRunner runner,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpMusicVideoDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MusicVideoDownload> DownloadAsync(string videoId, int? maxHeight, CancellationToken cancellationToken, Func<Task>? started = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        var workDirectory = Path.Combine(WorkRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var download = await _runner
                .DownloadVideoAsync(videoId, workDirectory, maxHeight, cancellationToken, started)
                .ConfigureAwait(false);

            if (!File.Exists(download.FilePath))
            {
                throw new YtDlpException(videoId, YtDlpErrorKind.Unknown, "yt-dlp reported a file that is not on disk.");
            }

            return new MusicVideoDownload(
                download.FilePath,
                File.Exists(download.ThumbnailPath) ? download.ThumbnailPath : null,
                download.DurationMs);
        }
        catch
        {
            RemoveWorkDirectory(workDirectory);

            throw;
        }
    }

    /// <inheritdoc />
    public void Discard(MusicVideoDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);

        var directory = Path.GetDirectoryName(download.VideoPath);

        if (directory is not null)
        {
            RemoveWorkDirectory(directory);
        }
    }

    private string WorkRoot() => Path.Combine(_options.CurrentValue.DownloadsDir, WorkRootName);

    /// <summary>Deletes a work folder, and only a folder of the work root: nothing else is ever deleted from here.</summary>
    private void RemoveWorkDirectory(string directory)
    {
        try
        {
            if (!PathRules.IsStrictlyInside(WorkRoot(), directory))
            {
                return;
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogRemoveFailed(_logger, directory, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the music video work folder {Directory}")]
    private static partial void LogRemoveFailed(ILogger logger, string directory, Exception exception);
}
