using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wondarr.Api.Paging;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Api.MusicVideos;

/// <summary>
/// The Match page's Music videos tab (ADR-0014, the 2026-11-19 amendment): the videos already in the video folder that
/// the scan could not settle, and the choices that settle them. A choice only records which song a file is; the file is
/// never renamed, moved or rewritten. While music videos are turned off every endpoint here answers 409.
/// </summary>
[ApiController]
[Route("api/v1/musicvideo/match")]
public sealed class MusicVideoMatchController : ControllerBase
{
    private const string DefaultSortKey = "path";

    private readonly IMusicVideoMatchService _matches;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoMatchController"/> class.</summary>
    /// <param name="matches">The Match page's service.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the on/off switch.</param>
    public MusicVideoMatchController(IMusicVideoMatchService matches, IOptionsMonitor<MusicVideoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(options);

        _matches = matches;
        _options = options;
    }

    /// <summary>Lists the files in one state, by path.</summary>
    /// <param name="state"><c>ambiguous</c> (the default), <c>unmatched</c> or <c>ignored</c>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>200 with the page; 400 for an unknown state; 409 while music videos are turned off.</returns>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(PagingResource<MusicVideoMatchRowResource>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PagingResource<MusicVideoMatchRowResource>>> GetFiles(
        [FromQuery] string? state,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var wanted = (state ?? "ambiguous").Trim().ToLowerInvariant() switch
        {
            "ambiguous" => (MusicVideoFileState?)MusicVideoFileState.Ambiguous,
            "unmatched" => MusicVideoFileState.Unmatched,
            "ignored" => MusicVideoFileState.Ignored,
            _ => null,
        };

        if (wanted is null)
        {
            ModelState.AddModelError("state", "state must be ambiguous, unmatched or ignored.");

            return ValidationProblem(ModelState);
        }

        // A work queue reads top to bottom: ascending unless the caller asks otherwise.
        var paging = Request.ToPagingSpec();

        if (!Request.Query.ContainsKey("sortDirection"))
        {
            paging = paging with { Descending = false };
        }

        var page = await _matches.ListAsync(wanted.Value, paging, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, MusicVideoMatchRowResource.From));
    }

    /// <summary>Counts the files per state, for the tab's badge.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>200 with the counts; 409 while music videos are turned off.</returns>
    [HttpGet("summary")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoMatchSummaryResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoMatchSummaryResource>> GetSummary(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var summary = await _matches.GetSummaryAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new MusicVideoMatchSummaryResource(
            summary.Pending,
            summary.Adopted,
            summary.Ambiguous,
            summary.Unmatched,
            summary.Ignored,
            summary.Missing,
            summary.Ambiguous + summary.Unmatched));
    }

    /// <summary>Searches the library's songs by artist and title, for picking one by hand.</summary>
    /// <param name="term">The words; each has to be in the title or the artist credit.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>200 with at most 25 songs; 409 while music videos are turned off.</returns>
    [HttpGet("songs")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(IReadOnlyList<MusicVideoMatchSongResource>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<MusicVideoMatchSongResource>>> SearchSongs(
        [FromQuery] string? term,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var songs = await _matches.SearchSongsAsync(term ?? string.Empty, cancellationToken).ConfigureAwait(false);

        return Ok(songs.Select(MusicVideoMatchSongResource.From).ToList());
    }

    /// <summary>Streams the image that lies beside a file's own video.</summary>
    /// <param name="id">The file's row id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>200 with the image; 404 when there is no such file or no image beside it; 409 while music videos are turned off.</returns>
    [HttpGet("{id:long}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetThumbnail(long id, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var path = await _matches.FindThumbnailAsync(id, cancellationToken).ConfigureAwait(false);

        if (path is null)
        {
            return NotFound();
        }

        var contentType = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        // A sidecar is the user's own file and may change: the browser may keep it, but has to ask whether it is still good.
        Response.Headers.CacheControl = "private, no-cache";

        return PhysicalFile(path, contentType);
    }

    /// <summary>
    /// Settles one file: links it to a song (<c>songId</c>, with <c>replace</c> to take the place of the song's current
    /// video), marks it as not a video of the library (<c>ignore</c>), or puts an ignored file back (<c>unignore</c>).
    /// </summary>
    /// <param name="id">The file's row id.</param>
    /// <param name="request">Exactly one of <c>songId</c>, <c>ignore</c> or <c>unignore</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// 200 with the file's state; 400 for a body that names none or several; 404 when the file or the song is unknown;
    /// 409 while music videos are turned off, when the song already has a video (and <c>replace</c> was not true), when a
    /// download for the song is in progress, when the file has gone, or when the old video could not be recycled.
    /// </returns>
    [HttpPost("{id:long}/resolve")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoMatchResolvedResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoMatchResolvedResource>> Resolve(
        long id,
        [FromBody] MusicVideoMatchResolveResource request,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var chosen = (request?.SongId is not null ? 1 : 0) + (request?.Ignore == true ? 1 : 0) + (request?.Unignore == true ? 1 : 0);

        if (request is null || chosen != 1 || request.SongId is <= 0)
        {
            ModelState.AddModelError("songId", "Exactly one of songId, ignore or unignore must be given.");

            return ValidationProblem(ModelState);
        }

        MusicVideoMatchResult result;

        if (request.SongId is { } songId)
        {
            result = await _matches.ResolveAsync(id, songId, request.Replace == true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await _matches.IgnoreAsync(id, request.Ignore == true, cancellationToken).ConfigureAwait(false);
        }

        return result.Outcome switch
        {
            MusicVideoMatchOutcome.Adopted => Ok(new MusicVideoMatchResolvedResource(MusicVideoMatchRowResource.WireName(MusicVideoFileState.Adopted), request.SongId)),
            MusicVideoMatchOutcome.Ignored => Ok(new MusicVideoMatchResolvedResource(MusicVideoMatchRowResource.WireName(MusicVideoFileState.Ignored), null)),
            MusicVideoMatchOutcome.Unignored => Ok(new MusicVideoMatchResolvedResource("queued", null)),
            MusicVideoMatchOutcome.FileNotFound or MusicVideoMatchOutcome.SongNotFound => NotFound(),
            _ => Problem(
                title: Title(result.Outcome),
                detail: result.Message ?? Title(result.Outcome),
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>Settles many files at once: <c>acceptTop</c> links each file to its top candidate when that song has no video; <c>ignore</c> ignores each.</summary>
    /// <param name="request">The files' ids and the action.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>200 with the counts; 400 for no ids, more than 500, or an unknown action; 409 while music videos are turned off.</returns>
    [HttpPost("bulk")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(MusicVideoMatchBulkResultResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MusicVideoMatchBulkResultResource>> Bulk(
        [FromBody] MusicVideoMatchBulkResource request,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Disabled();
        }

        var ids = request?.Ids?.Distinct().ToList() ?? [];

        if (ids.Count == 0 || ids.Any(id => id <= 0))
        {
            ModelState.AddModelError("ids", "ids must name at least one file, by its id.");

            return ValidationProblem(ModelState);
        }

        if (ids.Count > IMusicVideoMatchService.MaxBulkFiles)
        {
            ModelState.AddModelError("ids", $"ids may name at most {IMusicVideoMatchService.MaxBulkFiles} files.");

            return ValidationProblem(ModelState);
        }

        MusicVideoBulkAction? action = request!.Action?.Trim().ToLowerInvariant() switch
        {
            "accepttop" => MusicVideoBulkAction.AcceptTop,
            "ignore" => MusicVideoBulkAction.Ignore,
            _ => null,
        };

        if (action is null)
        {
            ModelState.AddModelError("action", "action must be acceptTop or ignore.");

            return ValidationProblem(ModelState);
        }

        var result = await _matches.BulkAsync(ids, action.Value, cancellationToken).ConfigureAwait(false);

        return Ok(new MusicVideoMatchBulkResultResource(result.Requested, result.Adopted, result.Ignored, result.Skipped));
    }

    private static string Title(MusicVideoMatchOutcome outcome) => outcome switch
    {
        MusicVideoMatchOutcome.SongHasVideo => "The song already has a video",
        MusicVideoMatchOutcome.DownloadInProgress => "Download in progress",
        MusicVideoMatchOutcome.FileMissing => "The file has gone",
        MusicVideoMatchOutcome.RecycleFailed => "The old video could not be recycled",
        MusicVideoMatchOutcome.NotIgnored => "The file is not ignored",
        _ => "Not possible",
    };

    private ObjectResult Disabled() =>
        Problem(
            title: MusicVideoController.DisabledMessage,
            detail: MusicVideoController.DisabledMessage,
            statusCode: StatusCodes.Status409Conflict);
}
