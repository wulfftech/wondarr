using Wondarr.Api.Middleware;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Songs;

/// <summary>
/// The add dialog's audition button: a fresh Deezer preview URL for a track, by Deezer id, by ISRC or
/// by the song it belongs to, or, for a Discover suggestion that has neither id, by its artist and title.
/// Preview URLs are never stored — they carry a signed token that expires
/// after about half an hour — so every answer is fetched live and
/// marked <c>no-store</c>.
/// </summary>
[ApiController]
[MetadataUnavailableFilter]
[Route("api/v1/preview")]
public sealed class PreviewController : ControllerBase
{
    /// <summary>The longest artist or title a search by name accepts.</summary>
    public const int MaxNameLength = 200;

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

    /// <summary>
    /// Reads a fresh preview URL for exactly one of a Deezer id, an ISRC or a stored song. With none of the
    /// three, <paramref name="artist"/> and <paramref name="title"/> together run a Deezer search and take the
    /// first result whose artist and title normalise equal. Given alongside an id or an ISRC they are ignored.
    /// </summary>
    /// <param name="deezerId">The Deezer track id.</param>
    /// <param name="isrc">The ISRC of the track.</param>
    /// <param name="songId">The id of a song already in the library.</param>
    /// <param name="artist">The artist, for a search by name when there is no Deezer id or ISRC.</param>
    /// <param name="title">The title, for a search by name when there is no Deezer id or ISRC.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The URL, or 404 when the track has no preview and the caller named no other way in.</returns>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PreviewResource>> GetPreview(
        long? deezerId,
        string? isrc,
        long? songId,
        string? artist,
        string? title,
        CancellationToken cancellationToken)
    {
        var named = (deezerId is not null ? 1 : 0)
            + (string.IsNullOrWhiteSpace(isrc) ? 0 : 1)
            + (songId is not null ? 1 : 0);

        if (artist?.Length > MaxNameLength || title?.Length > MaxNameLength)
        {
            return Problem(
                title: "artist or title is too long",
                detail: $"artist and title may each be at most {MaxNameLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var byName = !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title);

        if (named > 1 || (named == 0 && !byName))
        {
            return Problem(
                title: "Exactly one parameter is required",
                detail: "Give one of deezerId, isrc or songId, or both artist and title.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var trackId = named == 0
            ? await FindTrackIdByNameAsync(artist!, title!, cancellationToken).ConfigureAwait(false)
            : await ResolveTrackIdAsync(deezerId, isrc, songId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Searches Deezer for the song and returns the id of the first result whose artist and title normalise
    /// equal to the ones asked for. The result's own preview URL is not used: the search answer is cached,
    /// and the URL is signed, so the caller fetches a fresh one by id.
    /// </summary>
    private async Task<long?> FindTrackIdByNameAsync(string artist, string title, CancellationToken cancellationToken)
    {
        var wantedArtist = TextMatching.NormalizeArtist(artist);
        var wantedTitle = TextMatching.Normalize(title);

        var found = await _deezer
            .SearchTracksAsync($"{artist.Trim()} {title.Trim()}", 25, cancellationToken)
            .ConfigureAwait(false);

        var match = found.Data.FirstOrDefault(track =>
            TextMatching.NormalizeArtist(track.Artist.Name) == wantedArtist
            && TextMatching.Normalize(track.Title) == wantedTitle);

        return match?.Id;
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
