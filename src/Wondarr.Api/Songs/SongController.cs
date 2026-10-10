using Wondarr.Api.Middleware;
using Wondarr.Api.Paging;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Songs;

/// <summary>
/// The song pipeline over HTTP, in the *arr style: search without adding
/// (<c>POST /api/v1/song/lookup</c>), add by MusicBrainz or Deezer id, then list, edit, move between
/// albums and delete. Every rule lives in <see cref="ISongService"/> and <see cref="IIdentityResolver"/>;
/// this controller only maps resources and turns the services' failures into RFC 7807 problems.
/// </summary>
[ApiController]
[MetadataUnavailableFilter]
[Route("api/v1/song")]
public sealed class SongController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "added";

    /// <summary>What every song added over HTTP records as its origin.</summary>
    private const string AddedBy = "api";

    /// <summary>The longest search term the lookup accepts.</summary>
    private const int MaxTermLength = 500;

    /// <summary>How many candidates the lookup returns.</summary>
    private const int LookupLimit = 20;

    /// <summary>The album key of an artist's Singles pseudo-album.</summary>
    private const string SinglesKey = SongService.SinglesAlbumKey;

    /// <summary>The title of an artist's Singles pseudo-album.</summary>
    private const string SinglesTitle = "Singles";

    private readonly ISongService _songs;
    private readonly IIdentityResolver _resolver;
    private readonly WondarrDbContext _database;
    private readonly ISongDetailsService _details;
    private readonly ISongLyricsService _lyrics;

    /// <summary>Initialises a new instance of the <see cref="SongController"/> class.</summary>
    /// <param name="songs">The song service.</param>
    /// <param name="resolver">The identity resolver, for the lookup.</param>
    /// <param name="database">The database, for the lookup's already-in-the-library read.</param>
    /// <param name="details">The song page's data.</param>
    /// <param name="lyrics">The song's lyrics.</param>
    public SongController(
        ISongService songs,
        IIdentityResolver resolver,
        WondarrDbContext database,
        ISongDetailsService details,
        ISongLyricsService lyrics)
    {
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(lyrics);

        _songs = songs;
        _resolver = resolver;
        _database = database;
        _details = details;
        _lyrics = lyrics;
    }

    /// <summary>Lists songs, filtered and paged. Every filter is optional and they combine with AND.</summary>
    /// <param name="artistId">Only songs an artist is credited on, in any role.</param>
    /// <param name="monitored">Only songs with this monitored flag.</param>
    /// <param name="term">A case-insensitive substring of the title or the artist credit.</param>
    /// <param name="hasFile">Only songs that do (or do not) hold a file.</param>
    /// <param name="libraryId">Only songs filed in this library.</param>
    /// <param name="qualityProfileId">Only songs on this quality profile.</param>
    /// <param name="qualityId">Only songs whose file has this quality.</param>
    /// <param name="tag">Only songs carrying this tag.</param>
    /// <param name="cutoffMet">Only songs whose file meets (or misses) the profile's cutoff; songs without a file match neither.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<SongResource>>> GetSongs(
        long? artistId,
        bool? monitored,
        string? term,
        bool? hasFile,
        long? libraryId,
        long? qualityProfileId,
        long? qualityId,
        string? tag,
        bool? cutoffMet,
        CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var filter = new SongListFilter
        {
            ArtistId = artistId,
            Monitored = monitored,
            Term = term,
            HasFile = hasFile,
            LibraryId = libraryId,
            QualityProfileId = qualityProfileId,
            QualityId = qualityId,
            Tag = tag,
            CutoffMet = cutoffMet,
        };
        var page = await _songs.GetPageAsync(paging, filter, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, song => song.ToResource()));
    }

    /// <summary>Reads one song.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<SongResource>> GetSong(long id, CancellationToken cancellationToken)
    {
        var song = await _songs.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (song is null)
        {
            return NotFound();
        }

        // One extra read for a song owned through a reference library: the file's source then names
        // the library and the relative path, which a list would not pay a query per row for.
        SongReferenceFileDetails? reference = null;

        if (song.File is { SourceType: SourceTypes.Reference } file
            && SongFileSource.Parse(file.SourceType, file.SourceRef).ReferenceFileId is { } referenceFileId)
        {
            reference = await _details.GetReferenceFileAsync(referenceFileId, cancellationToken).ConfigureAwait(false);
        }

        return Ok(song.ToResource(reference));
    }

    /// <summary>
    /// Everything the song's own page shows that the song does not carry: release options, the
    /// MusicBrainz recording, Deezer's numbers, the reference-library row and lyrics availability.
    /// A source that fails or takes longer than five seconds leaves its section <see langword="null"/>.
    /// </summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    [HttpGet("{id:long}/details")]
    [Produces("application/json")]
    public async Task<ActionResult<SongDetailsResource>> GetSongDetails(long id, CancellationToken cancellationToken)
    {
        var details = await _details.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return details is null ? NotFound() : Ok(details.ToResource());
    }

    /// <summary>
    /// The song's lyrics: the <c>.lrc</c>/<c>.txt</c> sidecar next to its file, otherwise one LRCLIB
    /// lookup (remembered for a day). Never writes a sidecar.
    /// </summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    [HttpGet("{id:long}/lyrics")]
    [Produces("application/json")]
    public async Task<ActionResult<SongLyricsResource>> GetSongLyrics(long id, CancellationToken cancellationToken)
    {
        var lyrics = await _lyrics.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return lyrics is null ? NotFound() : Ok(lyrics.ToResource());
    }

    /// <summary>Adds one song, by MusicBrainz recording MBID or by Deezer track id.</summary>
    /// <param name="resource">Exactly one id, and where the song lands.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>201 and the new song, 409 when the library already holds it, 404 when no provider knows the id.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SongResource>> AddSong(
        [FromBody] SongAddResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var hasRecording = !string.IsNullOrWhiteSpace(resource.MbRecordingId);
        if (hasRecording == (resource.DeezerId is not null))
        {
            return Problem(
                title: "Exactly one song id is required",
                detail: "Give either mbRecordingId or deezerId, not both and not neither.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await _songs
                .AddAsync(
                    hasRecording ? resource.MbRecordingId : null,
                    resource.DeezerId,
                    new SongAddOptions
                    {
                        QualityProfileId = resource.QualityProfileId,
                        LibraryId = resource.LibraryId,
                        Monitored = resource.Monitored ?? true,
                        AddedBy = AddedBy,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome == SongAddOutcome.AlreadyExists)
            {
                return ConflictProblem(result.Song.Id);
            }

            var song = result.Song.ToResource();

            return CreatedAtAction(nameof(GetSong), new { id = song.Id }, song);
        }
        catch (SongNotFoundException exception)
        {
            return Problem(
                title: "Song not found",
                detail: exception.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Changes a song's monitored flag and quality profile.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="resource">The fields to change; a missing one is left alone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SongResource>> UpdateSong(
        long id,
        [FromBody] SongUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var updated = await _songs
                .UpdateAsync(id, resource.Monitored, resource.QualityProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (updated is null)
            {
                return NotFound();
            }

            // The update loads the row alone; the resource needs the artist and album too.
            var song = await _songs.GetAsync(id, cancellationToken).ConfigureAwait(false);

            return song is null ? NotFound() : Ok(song.ToResource());
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes a song.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 when it was deleted, 404 when the id is unknown.</returns>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteSong(long id, CancellationToken cancellationToken) =>
        await _songs.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    /// <summary>Searches for songs without adding them, for the add dialog.</summary>
    /// <param name="resource">The term to look up.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The ranked candidates; each one names the song it already is, when there is one.</returns>
    [HttpPost("lookup")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<SongLookupResource>>> Lookup(
        [FromBody] SongLookupRequest resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var term = resource.Term;

        if (string.IsNullOrWhiteSpace(term))
        {
            return Problem(
                title: "A search term is required",
                detail: "term must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (term.Length > MaxTermLength)
        {
            return Problem(
                title: "The search term is too long",
                detail: $"term must be at most {MaxTermLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // An id is resolved directly, so the lookup refuses what it cannot search rather than
        // answering with an empty list.
        var input = LookupInput.Parse(term);
        if (input.Kind == LookupKind.Unsupported)
        {
            return Problem(
                title: "Unsupported lookup",
                detail: input.UnsupportedReason ?? $"'{input.Raw}' cannot be looked up.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        PartialSearch<SongCandidate> found;

        try
        {
            found = await _resolver.SearchPartialAsync(term, LookupLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (ProvidersUnavailableException)
        {
            return MetadataUnavailableFilterAttribute.Unavailable(
                HttpContext,
                "Song search is unavailable",
                "MusicBrainz and Deezer did not answer; try again in a minute.");
        }

        var candidates = found.Items;

        if (found.IsPartial)
        {
            // The list is still the body, so a client that never heard of the header keeps working.
            Response.Headers[MetadataUnavailableFilterAttribute.PartialHeader] =
                MetadataUnavailableFilterAttribute.PartialValue(found.FailedProviders);
        }

        var existing = await SongLookupIndex
            .FindExistingAsync(_database, candidates, cancellationToken)
            .ConfigureAwait(false);

        return Ok(candidates
            .Select(candidate => candidate.ToResource(ExistingId(candidate, existing)))
            .ToList());
    }

    /// <summary>The releases a song could be filed under, plus its artist's Singles pseudo-album.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}/albumcontexts")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<AlbumOptionResource>>> GetAlbumContexts(
        long id,
        CancellationToken cancellationToken)
    {
        var song = await _songs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (song is null)
        {
            return NotFound();
        }

        var options = await _songs.GetAlbumOptionsAsync(id, cancellationToken).ConfigureAwait(false);
        var current = song.AlbumContext?.AlbumKey;
        var resources = options
            .Select(option => option.ToResource(string.Equals(option.Key, current, StringComparison.Ordinal)))
            .ToList();

        resources.Add(SinglesOption(song));

        return Ok(resources);
    }

    /// <summary>Moves one song to another album context, overriding the album policy.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="resource">The album key to move the song to.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    [HttpPut("{id:long}/albumcontext")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SongResource>> SetAlbumContext(
        long id,
        [FromBody] SongAlbumContextUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (string.IsNullOrWhiteSpace(resource.AlbumKey))
        {
            return Problem(
                title: "An album key is required",
                detail: "albumKey must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var moved = await _songs
                .SetAlbumContextAsync(id, resource.AlbumKey, cancellationToken)
                .ConfigureAwait(false);

            if (moved is null)
            {
                return NotFound();
            }

            // The assignment is written against the row alone; the resource needs the loaded song.
            var song = await _songs.GetAsync(id, cancellationToken).ConfigureAwait(false);

            return song is null ? NotFound() : Ok(song.ToResource());
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Unknown album key",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>The id of the song the candidate already is, or <see langword="null"/>.</summary>
    private static long? ExistingId(SongCandidate candidate, IReadOnlyDictionary<string, long> existing)
    {
        var key = SongLookupIndex.KeyOf(candidate);

        return key is not null && existing.TryGetValue(key, out var id) ? id : null;
    }

    /// <summary>The artist's Singles pseudo-album, which is not a release and so is appended by hand.</summary>
    private static AlbumOptionResource SinglesOption(Song song) =>
        new(
            SinglesKey,
            null,
            null,
            SinglesTitle,
            song.PrimaryArtist?.Name ?? song.ArtistCredit,
            null,
            [],
            null,
            null,
            null,
            null,
            song.AlbumContext?.Kind == AlbumContextKind.PseudoSingles,
            null,
            false,
            null,
            null);

    /// <summary>A 409 naming the song the library already holds, so the UI can link straight to it.</summary>
    private ObjectResult ConflictProblem(long songId)
    {
        var conflict = Problem(
            title: "Song already exists",
            detail: $"The library already holds this song as {songId}.",
            statusCode: StatusCodes.Status409Conflict);

        if (conflict.Value is ProblemDetails details)
        {
            details.Extensions["songId"] = songId;
        }

        return conflict;
    }

    /// <summary>Turns a service's validation failure into an RFC 7807 problem.</summary>
    private ObjectResult Invalid(ArgumentException exception) =>
        Problem(
            title: "Invalid request",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest);
}
