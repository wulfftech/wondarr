using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Api.MusicVideos;

/// <summary>
/// A song's music video (ADR-0014): read it, or remove it. While music videos are turned off every endpoint here
/// answers 409; the settings endpoints (<see cref="MusicVideoSettingsController"/>) still work, because that is
/// where the feature is turned on.
/// </summary>
[ApiController]
[Route("api/v1/musicvideo")]
public sealed class MusicVideoController : ControllerBase
{
    /// <summary>What every endpoint says while the feature is off.</summary>
    internal const string DisabledMessage = "Music videos are turned off";

    private readonly IMusicVideoService _videos;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoController"/> class.</summary>
    /// <param name="videos">Reads and removes the videos.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the on/off switch.</param>
    public MusicVideoController(IMusicVideoService videos, IOptionsMonitor<MusicVideoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(options);

        _videos = videos;
        _options = options;
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
    /// Removes a song's music video: the file and its <c>.nfo</c> and thumbnail go to the recycle bin, the YouTube
    /// video is blocklisted for the song (unless told not to), and the song has no video again.
    /// </summary>
    /// <param name="songId">The song id.</param>
    /// <param name="blocklist">Whether the YouTube video is blocklisted for the song (default true).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// 200 with what was done; 404 when the song has no video; 409 while music videos are turned off, or when the
    /// file is inside a reference library, its path is outside the music video folder or it is busy; 500 when it cannot
    /// be moved for another reason; 504 when the disk does not answer.
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

    private ObjectResult Disabled() =>
        Problem(title: DisabledMessage, detail: DisabledMessage, statusCode: StatusCodes.Status409Conflict);
}
