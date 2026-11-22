using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>Reads the title a YouTube video has now.</summary>
public interface IMusicVideoTitleSource
{
    /// <summary>Looks one video's title up.</summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The title, or <see langword="null"/> when there is none (the video is gone or private, the lookup failed).</returns>
    Task<string?> GetTitleAsync(string videoId, CancellationToken cancellationToken);
}

/// <summary>Fills the titles that the videos downloaded before M-66 never kept.</summary>
public interface IMusicVideoTitleBackfill
{
    /// <summary>
    /// Fills the missing <c>video_title</c> of some filed videos that have a YouTube id, at most
    /// <see cref="MusicVideoTitleBackfill.MaxPerRun"/> a run, two lookups at a time. A video whose title cannot be read is left as it is.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>How many titles were filled.</returns>
    Task<int> FillMissingAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoTitleBackfill : IMusicVideoTitleBackfill
{
    /// <summary>The most titles one scan looks up: the scan runs daily, so the rest wait for the next one.</summary>
    public const int MaxPerRun = 20;

    /// <summary>How many lookups run at once.</summary>
    public const int Concurrency = 2;

    private const int MaxTitleLength = 300;

    private readonly WondarrDbContext _database;
    private readonly IMusicVideoTitleSource _titles;
    private readonly ILogger<MusicVideoTitleBackfill> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoTitleBackfill"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="titles">Reads a video's title.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoTitleBackfill(WondarrDbContext database, IMusicVideoTitleSource titles, ILogger<MusicVideoTitleBackfill> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _titles = titles;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> FillMissingAsync(CancellationToken cancellationToken)
    {
        // A video that cannot be looked up stays in this list for every run, so the pick is random: one that never answers
        // cannot keep the others from being tried.
        var wanted = await _database.MusicVideos
            .AsNoTracking()
            .Where(video => video.Status == MusicVideoStatus.Present && video.YouTubeVideoId != string.Empty && video.VideoTitle == null)
            .OrderBy(_ => EF.Functions.Random())
            .Select(video => new { video.Id, video.YouTubeVideoId })
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (wanted.Count == 0)
        {
            return 0;
        }

        using var gate = new SemaphoreSlim(Concurrency);

        var lookups = wanted.Select(async video =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (video.Id, Title: await _titles.GetTitleAsync(video.YouTubeVideoId, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogLookupFailed(_logger, video.YouTubeVideoId, exception);

                return (video.Id, Title: null);
            }
            finally
            {
                gate.Release();
            }
        });

        var answers = await Task.WhenAll(lookups).ConfigureAwait(false);
        var filled = 0;

        foreach (var (id, title) in answers)
        {
            var clipped = Clip(title);

            if (clipped is null)
            {
                continue;
            }

            // Only a row that still has no title: a download that filed one meanwhile is the newer word. The row's
            // updated_at is its status's clock and is left alone.
            filled += await _database.MusicVideos
                .Where(video => video.Id == id && video.VideoTitle == null)
                .ExecuteUpdateAsync(set => set.SetProperty(video => video.VideoTitle, clipped), cancellationToken)
                .ConfigureAwait(false);
        }

        return filled;
    }

    private static string? Clip(string? title)
    {
        var trimmed = title?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxTitleLength ? trimmed[..MaxTitleLength] : trimmed;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The title of the music video {VideoId} could not be read; it is left empty")]
    private static partial void LogLookupFailed(ILogger logger, string videoId, Exception exception);
}
