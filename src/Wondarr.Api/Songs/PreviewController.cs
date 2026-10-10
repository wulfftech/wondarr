using Wondarr.Api.Middleware;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Songs;

/// <summary>
/// The add dialog's audition button: a fresh Deezer preview URL for a track, by Deezer id, by ISRC or
/// by the song it belongs to. Preview URLs are never stored — they carry a signed token that expires
/// after about half an hour (DECISIONS, build session 2 #7) — so every answer is fetched live and
/// marked <c>no-store</c>.
/// </summary>
[ApiController]
[MetadataUnavailableFilter]
[Route("api/v1/preview")]
public sealed class PreviewController : ControllerBase
{
    private readonly IDeezerClient _deezer;
    private readonly ISongService _songs;

    /// <summary>Initialises a new instance of the <see cref="PreviewController"/> class.</summary>
    /// <param name="deezer">The Deezer client.</param>
    /// <param name="songs">The song service, for the Deezer-id-or-ISRC lookup of a stored song.</param>
    public PreviewController(IDeezerClient deezer, ISongService songs)
    {
        ArgumentNullException.ThrowIfNull(deezer);
        ArgumentNullException.ThrowIfNull(songs);

        _deezer = deezer;
        _songs = songs;
    }

    /// <summary>Reads a fresh preview URL for exactly one of a Deezer id, an ISRC or a stored song.</summary>
    /// <param name="deezerId">The Deezer track id.</param>
    /// <param name="isrc">The ISRC of the track.</param>
    /// <param name="songId">The id of a song already in the library.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The URL, or 404 when the track has no preview and the caller named no other way in.</returns>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PreviewResource>> GetPreview(
        long? deezerId,
        string? isrc,
        long? songId,
        CancellationToken cancellationToken)
    {
        var named = (deezerId is not null ? 1 : 0)
            + (string.IsNullOrWhiteSpace(isrc) ? 0 : 1)
            + (songId is not null ? 1 : 0);

        if (named != 1)
        {
            return Problem(
                title: "Exactly one parameter is required",
                detail: "Give one of deezerId, isrc or songId.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var trackId = await ResolveTrackIdAsync(deezerId, isrc, songId, cancellationToken).ConfigureAwait(false);
        if (trackId is null)
        {
            return NotFound();
        }

        var url = await _deezer
            .GetFreshPreviewUrlAsync(trackId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(url))
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new PreviewResource(url));
    }

    /// <summary>The Deezer track id the request refers to, or <see langword="null"/> when there is none.</summary>
    private async Task<long?> ResolveTrackIdAsync(
        long? deezerId,
        string? isrc,
        long? songId,
        CancellationToken cancellationToken)
    {
        if (deezerId is { } id)
        {
            return id;
        }

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var track = await _deezer.GetTrackByIsrcAsync(isrc, cancellationToken).ConfigureAwait(false);

            return track?.Id;
        }

        var song = await _songs.GetAsync(songId!.Value, cancellationToken).ConfigureAwait(false);
        if (song is null)
        {
            return null;
        }

        if (song.DeezerId is { } songDeezerId)
        {
            return songDeezerId;
        }

        // A MusicBrainz-only song has no Deezer id, so its ISRC is the only way to Deezer's track.
        var songIsrc = song.Isrcs.Find(value => !string.IsNullOrWhiteSpace(value));
        if (songIsrc is null)
        {
            return null;
        }

        var byIsrc = await _deezer.GetTrackByIsrcAsync(songIsrc, cancellationToken).ConfigureAwait(false);

        return byIsrc?.Id;
    }
}
