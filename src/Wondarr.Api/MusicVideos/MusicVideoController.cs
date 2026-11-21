using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wondarr.Api.Middleware;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Sources.YouTube;

namespace Wondarr.Api.MusicVideos;

/// <summary>
/// A song's music video (ADR-0014): read it, find it, pick one, or remove it. While music videos are turned off every
/// endpoint here answers 409; the settings endpoints (<see cref="MusicVideoSettingsController"/>) still work, because
/// that is where the feature is turned on.
/// </summary>
[ApiController]
[MetadataUnavailableFilter]
[Route("api/v1/musicvideo")]
public sealed partial class MusicVideoController : ControllerBase
{
    /// <summary>What every endpoint says while the feature is off.</summary>
    internal const string DisabledMessage = "Music videos are turned off";

    /// <summary>The most songs one bulk search may name.</summary>
    internal const int MaxBulkSongs = 200;

    private readonly IMusicVideoService _videos;
    private readonly IMusicVideoSearchService _search;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoController"/> class.</summary>
    /// <param name="videos">Reads and removes the videos.</param>
    /// <param name="search">Finds videos and queues their downloads.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the on/off switch.</param>
    /// <param name="commands">Queues the scan of the video folder.</param>
    public MusicVideoController(
        IMusicVideoService videos,
        IMusicVideoSearchService search,
        IOptionsMonitor<MusicVideoOptions> options,
        ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commands);

        _videos = videos;
        _search = search;
        _options = options;
        _commands = commands;
    }

    /// <summary>
    /// Queues a scan of the video folder: every video in it is identified against the library's songs, a confident match
    /// is linked where it lies, and the rest wait on the Match page. A scan already queued or running is the answer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns>202 with the queued command; 409 while music videos are turned off.</returns>
    [HttpPost("scan")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoScanAcceptedResource), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoScanAcceptedResource>> Scan(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var command = await _commands
            .EnqueueAsync(MusicVideoScanCommandHandler.CommandName, null, CommandTrigger.Manual, cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new MusicVideoScanAcceptedResource(command.Id));
    }

    /// <summary>Reads a song's music video.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>200 with the video; 404 when the song has none; 409 while music videos are turned off.</returns>
    [HttpGet("song/{songId:long}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoResource), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoResource>> GetVideo(long songId, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var video = await _videos.GetAsync(songId, cancellationToken).ConfigureAwait(false);

        return video is null ? NotFound() : Ok(MusicVideoResource.From(video));
    }

    /// <summary>
    /// Searches MusicBrainz and YouTube Music for a song's music video. A confident candidate (MusicBrainz links it
    /// to the song, or it is an official video of the song's artist titled as the song) has its download queued at
    /// once, and <c>queued</c> is true. Otherwise only the ranked candidates come back, for the person to pick from.
    /// </summary>
    /// <param name="songId">The song id.</param>
    /// <param name="autoDownload">
    /// Whether a confident candidate is downloaded at once (default true). False only lists the candidates, for a person
    /// who wants to pick one.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// 200 with whether a download was queued and the candidates; 404 when there is no such song; 409 while music
    /// videos are turned off; 502 when YouTube Music cannot be searched; 503 when MusicBrainz is unavailable.
    /// </returns>
    [HttpPost("song/{songId:long}/search")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoSearchResultResource), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<MusicVideoSearchResultResource>> SearchSong(
        long songId,
        [FromQuery] bool autoDownload = true,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        MusicVideoSearchResult? result;

        try
        {
            result = await _search.SearchAsync(songId, cancellationToken).ConfigureAwait(false);
        }
        catch (InnertubeException exception)
        {
            return Problem(title: "YouTube Music could not be searched", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        if (result is null)
        {
            return NotFound();
        }

        var queued = false;

        if (autoDownload && result.AutoDownload is { } confident)
        {
            var outcome = await _search
                .QueueDownloadAsync(songId, confident.VideoId, confident.FoundBy, CommandTrigger.Manual, cancellationToken, videoTitle: confident.Title)
                .ConfigureAwait(false);

            queued = outcome.Outcome == MusicVideoQueueOutcome.Queued;
        }

        return Ok(new MusicVideoSearchResultResource(queued, [.. result.Candidates.Select(MusicVideoCandidateResource.From)], result.Partial));
    }

    /// <summary>
    /// Queues the download of a video the person picked, as a manual find. A song that holds a different video has
    /// it replaced once the new one is downloaded and checked.
    /// </summary>
    /// <param name="songId">The song id.</param>
    /// <param name="resource">The video id, or a link to the video.</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns>
    /// 202 with the queued command; 400 when the id is not a YouTube video id; 404 when there is no such song; 409 while
    /// music videos are turned off, when the video is blocklisted for the song, when a download for the song is already
    /// in progress, or when the song already holds that video.
    /// </returns>
    [HttpPost("song/{songId:long}/grab")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoGrabAcceptedResource), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoGrabAcceptedResource>> Grab(
        long songId,
        [FromBody] MusicVideoGrabResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        if (!TryReadVideoId(resource.YouTubeVideoId, out var videoId))
        {
            ModelState.AddModelError("youTubeVideoId", "youTubeVideoId must be a YouTube video id (11 characters) or a link to one.");

            return ValidationProblem(ModelState);
        }

        var queued = await _search
            .QueueDownloadAsync(songId, videoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, cancellationToken, replace: true, videoTitle: resource.VideoTitle)
            .ConfigureAwait(false);

        return queued.Outcome switch
        {
            MusicVideoQueueOutcome.Queued => Accepted(new MusicVideoGrabAcceptedResource(queued.CommandId ?? 0)),
            MusicVideoQueueOutcome.SongNotFound => NotFound(),
            MusicVideoQueueOutcome.Blocklisted => ConflictProblem("Blocklisted", "That video was removed as the wrong video for this song, so it is not downloaded again."),
            MusicVideoQueueOutcome.InProgress => ConflictProblem("Download in progress", "A download is in progress"),
            _ => ConflictProblem("Already filed", "The song already has this video."),
        };
    }

    /// <summary>
    /// Queues a search for each of the named songs. Only a confident match is downloaded; the rest are left to be
    /// picked by hand. A song that does not exist is skipped.
    /// </summary>
    /// <param name="resource">The songs, at most 200.</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns>
    /// 202 with how many searches were queued; 400 when no song, or more than 200, is named; 409 while music videos
    /// are turned off.
    /// </returns>
    [HttpPost("search")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoBulkSearchAcceptedResource), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoBulkSearchAcceptedResource>> SearchSongs(
        [FromBody] MusicVideoBulkSearchResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var songIds = resource.SongIds?.Distinct().ToList() ?? [];

        if (songIds.Count == 0 || songIds.Any(id => id <= 0))
        {
            ModelState.AddModelError("songIds", "songIds must name at least one song, by its id.");

            return ValidationProblem(ModelState);
        }

        if (songIds.Count > MaxBulkSongs)
        {
            ModelState.AddModelError("songIds", $"songIds may name at most {MaxBulkSongs} songs.");

            return ValidationProblem(ModelState);
        }

        var commandIds = await _search.QueueSearchesAsync(songIds, CommandTrigger.Manual, cancellationToken).ConfigureAwait(false);

        return Accepted(new MusicVideoBulkSearchAcceptedResource(commandIds.Count, commandIds));
    }

    /// <summary>
    /// Removes a song's music video: the file and its <c>.nfo</c> and thumbnail go to the recycle bin, the YouTube
    /// video is blocklisted for the song (unless told not to), and the song has no video again.
    /// </summary>
    /// <param name="songId">The song id.</param>
    /// <param name="blocklist">Whether the YouTube video is blocklisted for the song (default true).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// 200 with what was done; 404 when the song has no video; 409 while music videos are turned off, while a download
    /// for the song is queued or running, when the file is inside a reference library, its path is outside the music
    /// video folder or it is busy; 500 when it cannot be moved for another reason; 504 when the disk does not answer.
    /// </returns>
    [HttpDelete("song/{songId:long}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoRemovedResource), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoRemovedResource>> DeleteVideo(
        long songId,
        [FromQuery] bool blocklist = true,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        try
        {
            var removal = await _videos.DeleteAsync(songId, blocklist, cancellationToken).ConfigureAwait(false);

            return removal is null
                ? NotFound()
                : Ok(new MusicVideoRemovedResource(removal.Path, removal.RecycledPaths, removal.Blocklisted));
        }
        catch (MusicVideoDownloadInProgressException exception)
        {
            return Problem(title: "Download in progress", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MusicVideoInReferenceLibraryException exception)
        {
            return Problem(title: "Your own copy", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MusicVideoPathException exception)
        {
            return Problem(title: "Path not allowed", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MusicVideoFileException exception)
        {
            // Busy or locked is the user's to retry; anything else is ours.
            return exception.Busy
                ? Problem(title: "File busy", detail: exception.Message, statusCode: StatusCodes.Status409Conflict)
                : Problem(title: "Could not remove the video", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (MusicVideoTimeoutException exception)
        {
            return Problem(title: exception.Message, detail: exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    /// <summary>Reads a video id from what the person sent: the id itself, or a link to the video.</summary>
    private static bool TryReadVideoId(string? text, out string videoId)
    {
        var trimmed = text?.Trim();

        if (!string.IsNullOrEmpty(trimmed) && BareVideoId().IsMatch(trimmed))
        {
            videoId = trimmed;

            return true;
        }

        return YouTubeVideoLinks.TryParse(trimmed, out videoId);
    }

    private ObjectResult ConflictProblem(string title, string detail) =>
        Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    private ObjectResult Disabled() =>
        Problem(title: DisabledMessage, detail: DisabledMessage, statusCode: StatusCodes.Status409Conflict);

    /// <summary>A YouTube video id on its own: eleven URL-safe base64 characters and nothing after them.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BareVideoId();
}
