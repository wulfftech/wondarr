using System.Globalization;
using Wondarr.Sources.YouTube;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.YouTube;

/// <summary>
/// Reads one video's details for the Match queue's "Add to MusicBrainz" form (DECISIONS build session 14
/// #33): the channel and upload date of the link a person pasted. The pasted text is never handed to
/// yt-dlp; only the video id read out of it is.
/// </summary>
[ApiController]
[Route("api/v1/youtube/video")]
public sealed class YouTubeVideoController : ControllerBase
{
    private readonly IYtDlpRunner _ytDlp;
    private readonly IOptionsMonitor<YouTubeOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="YouTubeVideoController"/> class.</summary>
    /// <param name="ytDlp">Runs the read, behind the same gate as the downloads.</param>
    /// <param name="options">Whether the YouTube source is on: off, Wondarr does not read YouTube.</param>
    public YouTubeVideoController(IYtDlpRunner ytDlp, IOptionsMonitor<YouTubeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(ytDlp);
        ArgumentNullException.ThrowIfNull(options);

        _ytDlp = ytDlp;
        _options = options;
    }

    /// <summary>Reads the details of one YouTube video.</summary>
    /// <param name="url">A link to the video: watch, youtu.be, shorts, live or embed.</param>
    /// <param name="cancellationToken">Cancels the wait and the read.</param>
    /// <returns>
    /// 200 with the details; 400 when the link is not one YouTube video; 409 while the YouTube source is
    /// off; 404 for a private or removed video; 503 when yt-dlp is missing, busy or refused.
    /// </returns>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<YouTubeVideoResource>> GetVideo(
        [FromQuery] string? url,
        CancellationToken cancellationToken)
    {
        if (!YouTubeVideoLinks.TryParse(url, out var videoId))
        {
            ModelState.AddModelError("url", "url must be a link to one YouTube video.");

            return ValidationProblem(ModelState);
        }

        if (!_options.CurrentValue.Enabled)
        {
            return Problem(
                title: "The YouTube source is off",
                detail: "Wondarr reads YouTube only while the YouTube source is on (Settings → YouTube).",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            var video = await _ytDlp.ReadVideoAsync(videoId, cancellationToken).ConfigureAwait(false);

            return Ok(new YouTubeVideoResource(
                video.VideoId,
                YouTubeVideoLinks.WatchUrl(video.VideoId),
                video.Title,
                video.Channel,
                video.UploadDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                video.DurationMs));
        }
        catch (YtDlpException exception)
        {
            return Problem(
                title: "The video's details could not be read",
                detail: exception.Message,
                statusCode: exception.Kind switch
                {
                    YtDlpErrorKind.PrivateOrUnavailable => StatusCodes.Status404NotFound,
                    YtDlpErrorKind.Unknown => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status503ServiceUnavailable,
                });
        }
    }
}
