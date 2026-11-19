using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// <c>MusicVideoSearch</c>: looks for one song's music video and, with <c>autoDownload</c>, downloads a confident
/// match (ADR-0014). Anything less confident is left to the person to pick, so it is not downloaded here.
/// </summary>
public sealed partial class MusicVideoSearchCommandHandler : ICommandHandler
{
    /// <summary>The name the API and the task list use.</summary>
    public const string CommandName = "MusicVideoSearch";

    private readonly IMusicVideoSearchService _search;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ILogger<MusicVideoSearchCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoSearchCommandHandler"/> class.</summary>
    /// <param name="search">Searches and queues the download.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the on/off switch.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoSearchCommandHandler(
        IMusicVideoSearchService search,
        IOptionsMonitor<MusicVideoOptions> options,
        ILogger<MusicVideoSearchCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _search = search;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var body = MusicVideoCommandBodies.Read<MusicVideoCommandBodies.Search>(context.Body, CommandName);
        var prefix = $"Song {body.SongId.ToString(CultureInfo.InvariantCulture)}: ";

        if (!_options.CurrentValue.Enabled)
        {
            return prefix + "music videos are turned off.";
        }

        var result = await _search.SearchAsync(body.SongId, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return prefix + "no such song.";
        }

        if (result.AutoDownload is not { } confident)
        {
            return prefix + string.Create(CultureInfo.InvariantCulture, $"no confident match among {result.Candidates.Count} candidate(s).");
        }

        if (!body.AutoDownload)
        {
            return prefix + $"confident match {confident.VideoId}; not downloaded.";
        }

        var queued = await _search
            .QueueDownloadAsync(body.SongId, confident.VideoId, confident.FoundBy, context.Trigger, cancellationToken)
            .ConfigureAwait(false);

        LogSearched(_logger, body.SongId, confident.VideoId, queued.Outcome);

        return prefix + (queued.Outcome == MusicVideoQueueOutcome.Queued
            ? $"queued the download of {confident.VideoId}."
            : $"confident match {confident.VideoId} not queued ({queued.Outcome}).");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Searched for the music video of song {SongId}: {VideoId} ({Outcome})")]
    private static partial void LogSearched(ILogger logger, long songId, string videoId, MusicVideoQueueOutcome outcome);
}

/// <summary>
/// <c>MusicVideoDownload</c>: downloads, checks and files one chosen video for a song
/// (<see cref="IMusicVideoImporter"/>).
/// </summary>
public sealed class MusicVideoDownloadCommandHandler : ICommandHandler
{
    /// <summary>The name the queue and the task list use.</summary>
    public const string CommandName = "MusicVideoDownload";

    private readonly IMusicVideoImporter _importer;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoDownloadCommandHandler"/> class.</summary>
    /// <param name="importer">Downloads, checks and files the video.</param>
    public MusicVideoDownloadCommandHandler(IMusicVideoImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);

        _importer = importer;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var body = MusicVideoCommandBodies.Read<MusicVideoCommandBodies.Download>(context.Body, CommandName);

        if (string.IsNullOrWhiteSpace(body.YouTubeVideoId))
        {
            throw new ArgumentException($"The {CommandName} command body must name a youTubeVideoId.");
        }

        return await _importer
            .ImportAsync(body.SongId, body.YouTubeVideoId, body.FoundBy, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// <c>MusicVideoMissingSearch</c>, the scheduled "Find missing music videos" task: works through the monitored songs
/// that have a file and no video, oldest added first, up to <c>scheduled_search_limit</c> songs, and downloads only
/// confident matches. A song with nothing to download is left alone for 30 days.
/// </summary>
public sealed partial class MusicVideoMissingSearchCommandHandler : ICommandHandler
{
    /// <summary>The name the scheduler and the task list use.</summary>
    public const string CommandName = "MusicVideoMissingSearch";

    /// <summary>How many failed lookups in a row end a run: a source that is down is not asked for every song.</summary>
    internal const int MaxConsecutiveErrors = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ILogger<MusicVideoMissingSearchCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoMissingSearchCommandHandler"/> class.</summary>
    /// <param name="scopes">Builds the scope each song's search runs in, so a long run keeps no database context.</param>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoMissingSearchCommandHandler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<MusicVideoOptions> options,
        ILogger<MusicVideoMissingSearchCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return "Music videos are turned off.";
        }

        IReadOnlyList<long> songIds;

        await using (var scope = _scopes.CreateAsyncScope())
        {
            songIds = await scope.ServiceProvider
                .GetRequiredService<IMusicVideoSearchService>()
                .NextMissingAsync(options.ScheduledSearchLimit, cancellationToken)
                .ConfigureAwait(false);
        }

        var queued = 0;
        var fruitless = 0;
        var errors = 0;
        var partialSearches = 0;
        var consecutiveErrors = 0;

        foreach (var songId in songIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (consecutiveErrors >= MaxConsecutiveErrors)
            {
                break;
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var search = scope.ServiceProvider.GetRequiredService<IMusicVideoSearchService>();
                var result = await search.SearchAsync(songId, cancellationToken).ConfigureAwait(false);

                if (result is null)
                {
                    continue;
                }

                consecutiveErrors = 0;

                // Only a confident match is downloaded by a schedule; the rest wait for a person to pick.
                if (result.AutoDownload is { } confident
                    && (await search
                        .QueueDownloadAsync(songId, confident.VideoId, confident.FoundBy, CommandTrigger.Scheduled, cancellationToken)
                        .ConfigureAwait(false)).Outcome == MusicVideoQueueOutcome.Queued)
                {
                    queued++;

                    continue;
                }

                // MusicBrainz did not answer: "no confident match" is not an answer, so the song is tried again.
                if (result.Partial)
                {
                    partialSearches++;

                    continue;
                }

                await search.RememberFruitlessAsync(songId, cancellationToken).ConfigureAwait(false);
                fruitless++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors++;
                consecutiveErrors++;
                LogSearchFailed(_logger, songId, exception);
            }
        }

        var searched = queued + fruitless + partialSearches;
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Searched {searched} of {songIds.Count} song(s): queued {queued} download(s), {fruitless} without a confident match.");

        if (partialSearches > 0)
        {
            message = string.Create(CultureInfo.InvariantCulture, $"{message} {partialSearches} without a MusicBrainz answer, not remembered.");
        }

        return errors > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{message} {errors} search(es) failed.")
            : message;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video search for song {SongId} failed")]
    private static partial void LogSearchFailed(ILogger logger, long songId, Exception exception);
}

/// <summary>
/// Puts <c>MusicVideoScan</c> on the scheduler while music videos are on, and <c>MusicVideoMissingSearch</c> too while the
/// scheduled search is on. The settings are read live, so a change adds or removes the tasks without a restart.
/// </summary>
public sealed class MusicVideoScheduledTasks : IScheduledTaskSource
{
    private readonly IOptionsMonitor<MusicVideoOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoScheduledTasks"/> class.</summary>
    /// <param name="options">The <c>music_videos</c> settings, which decide whether and how often the task runs.</param>
    public MusicVideoScheduledTasks(IOptionsMonitor<MusicVideoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDefinition> Tasks
    {
        get
        {
            var options = _options.CurrentValue;

            if (!options.Enabled)
            {
                return [];
            }

            // The scan of the video folder runs daily while the feature is on; the search for missing videos only
            // when its own switch is on too.
            List<ScheduledTaskDefinition> tasks = [new(MusicVideoScanCommandHandler.CommandName, MusicVideoScanCommandHandler.Interval)];

            if (options.ScheduledSearchEnabled)
            {
                tasks.Add(new(MusicVideoMissingSearchCommandHandler.CommandName, TimeSpan.FromHours(options.ScheduledSearchIntervalHours)));
            }

            return tasks;
        }
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return _options.OnChange(_ => listener()) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
