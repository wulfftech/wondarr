using System.Globalization;
using System.Text;
using Compilarr.Core.Metadata.Deezer;
using Compilarr.Core.Metadata.ITunes;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Metadata.CoverArt;

/// <summary>What is known about the item whose cover art is wanted.</summary>
public sealed record CoverArtRequest
{
    /// <summary>Gets the release group MBID, when the item has one.</summary>
    public string? MbReleaseGroupId { get; init; }

    /// <summary>Gets the release MBID, when the item has one.</summary>
    public string? MbReleaseId { get; init; }

    /// <summary>Gets the Deezer album id, when the item came from Deezer.</summary>
    public long? DeezerAlbumId { get; init; }

    /// <summary>Gets the artist name. The artwork of a same-named single by someone else is wrong.</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the album name, when the item belongs to one.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the track title, which stands in for the album on a loose single.</summary>
    public string? Title { get; init; }
}

/// <summary>Where a cover was found, and by whom.</summary>
/// <param name="Url">The image URL.</param>
/// <param name="Source">The provider that supplied it: <c>coverartarchive</c>, <c>deezer</c> or <c>itunes</c>.</param>
public sealed record CoverArt(string Url, string Source);

/// <summary>Finds cover art for one item, trying the providers in order of how trustworthy they are.</summary>
public interface ICoverArtResolver
{
    /// <summary>Resolves cover art.</summary>
    /// <param name="request">What is known about the item.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The cover, or <see langword="null"/> when no provider has one.</returns>
    Task<CoverArt?> ResolveAsync(CoverArtRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The cover-art chain: Cover Art Archive for the release group, then for the release, then Deezer's
/// album cover, then a Deezer search, then iTunes. A provider that fails is logged and skipped — a
/// missing cover is a normal outcome, not an error the import should die of.
/// </summary>
public sealed partial class CoverArtResolver : ICoverArtResolver
{
    /// <summary>The source reported when the Cover Art Archive supplied the cover.</summary>
    public const string CoverArtArchiveSource = CoverArtArchiveClient.Provider;

    /// <summary>The source reported when Deezer supplied the cover.</summary>
    public const string DeezerSource = DeezerClient.Provider;

    /// <summary>The source reported when iTunes supplied the cover.</summary>
    public const string ITunesSource = ITunesClient.Provider;

    /// <summary>How many Deezer search hits to consider before giving up on the artist match.</summary>
    private const int SearchLimit = 25;

    /// <summary>The size iTunes artwork is rewritten to.</summary>
    private const int ArtworkSize = 600;

    private readonly ICoverArtArchiveClient _coverArtArchive;
    private readonly IDeezerClient _deezer;
    private readonly IITunesClient _itunes;
    private readonly ILogger<CoverArtResolver> _logger;

    /// <summary>Initialises a new instance of the <see cref="CoverArtResolver"/> class.</summary>
    /// <param name="coverArtArchive">The Cover Art Archive client.</param>
    /// <param name="deezer">The Deezer client.</param>
    /// <param name="itunes">The iTunes client.</param>
    /// <param name="logger">The logger failures are reported to.</param>
    public CoverArtResolver(
        ICoverArtArchiveClient coverArtArchive,
        IDeezerClient deezer,
        IITunesClient itunes,
        ILogger<CoverArtResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(coverArtArchive);
        ArgumentNullException.ThrowIfNull(deezer);
        ArgumentNullException.ThrowIfNull(itunes);
        ArgumentNullException.ThrowIfNull(logger);

        _coverArtArchive = coverArtArchive;
        _deezer = deezer;
        _itunes = itunes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CoverArt?> ResolveAsync(CoverArtRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.MbReleaseGroupId))
        {
            var url = await TryGetUrlAsync(
                () => _coverArtArchive.GetReleaseGroupFrontUrlAsync(request.MbReleaseGroupId, cancellationToken))
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(url))
            {
                return new CoverArt(url, CoverArtArchiveSource);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.MbReleaseId))
        {
            var url = await TryGetUrlAsync(
                () => _coverArtArchive.GetReleaseFrontUrlAsync(request.MbReleaseId, cancellationToken))
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(url))
            {
                return new CoverArt(url, CoverArtArchiveSource);
            }
        }

        if (request.DeezerAlbumId is { } albumId && albumId > 0)
        {
            var album = await TryAsync<DeezerAlbum>(
                async () => await _deezer.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(album?.CoverXl))
            {
                return new CoverArt(album!.CoverXl!, DeezerSource);
            }
        }

        var deezerHit = await SearchDeezerAsync(request, cancellationToken).ConfigureAwait(false);
        if (deezerHit is not null)
        {
            return new CoverArt(deezerHit, DeezerSource);
        }

        var itunesHit = await SearchITunesAsync(request, cancellationToken).ConfigureAwait(false);
        if (itunesHit is not null)
        {
            return new CoverArt(itunesHit, ITunesSource);
        }

        return null;
    }

    /// <summary>
    /// Searches Deezer for "{artist} {album}", taking the first hit credited to the wanted artist.
    /// The same-named single by a tribute act is exactly what the artist check is for.
    /// </summary>
    private async Task<string?> SearchDeezerAsync(CoverArtRequest request, CancellationToken cancellationToken)
    {
        var query = BuildQuery(request.Artist, request.Album ?? request.Title);
        if (query.Length == 0)
        {
            return null;
        }

        var result = await TryAsync<DeezerSearchResult>(
                async () => await _deezer.SearchTracksAsync(query, SearchLimit, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (result?.Data is null)
        {
            return null;
        }

        foreach (var track in result.Data)
        {
            if (IsSameArtist(track.Artist?.Name, request.Artist) && !string.IsNullOrWhiteSpace(track.Album?.CoverXl))
            {
                return track.Album.CoverXl;
            }
        }

        return null;
    }

    /// <summary>Searches iTunes for "{artist} {title}", taking the first hit by the wanted artist.</summary>
    private async Task<string?> SearchITunesAsync(CoverArtRequest request, CancellationToken cancellationToken)
    {
        var query = BuildQuery(request.Artist, request.Title ?? request.Album);
        if (query.Length == 0)
        {
            return null;
        }

        var results = await TryAsync<IReadOnlyList<ITunesTrack>>(
                async () => await _itunes.SearchSongsAsync(query, SearchLimit, "US", cancellationToken)
                    .ConfigureAwait(false))
            .ConfigureAwait(false);

        if (results is null)
        {
            return null;
        }

        foreach (var track in results)
        {
            if (!IsSameArtist(track.ArtistName, request.Artist))
            {
                continue;
            }

            var url = ITunesClient.ToArtworkUrl(track.ArtworkUrl100, ArtworkSize);
            if (url is not null)
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>Joins the two non-empty halves of a search query with a single space.</summary>
    private static string BuildQuery(string? left, string? right)
    {
        var parts = new[] { left, right }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Compares artist names the way a human would: case, accents and padding are noise. A tribute
    /// act's "Get Lucky" is credited to "Get Lucky", which does not read as "Daft Punk".
    /// </summary>
    private static bool IsSameArtist(string? left, string? right)
    {
        var normalizedLeft = NormalizeArtist(left);

        return normalizedLeft.Length > 0 && normalizedLeft == NormalizeArtist(right);
    }

    /// <summary>Lower-cases a name and strips its diacritics, so "Björk" and "bjork" compare equal.</summary>
    private static string NormalizeArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // FormD splits "ö" into "o" + a combining mark; dropping the marks leaves the base letters.
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Runs one provider call, turning its failure into a miss. The log names the provider and the
    /// status only: a URL would carry the lookup, and the message would carry response headers.
    /// </summary>
    private async Task<string?> TryGetUrlAsync(Func<Task<string?>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (MetadataProviderException exception)
        {
            LogProviderFailure(_logger, exception.Provider, (int)exception.StatusCode);

            return null;
        }
    }

    /// <summary>Runs one provider call whose answer is an object, turning its failure into a miss.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T?>> call)
        where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (MetadataProviderException exception)
        {
            LogProviderFailure(_logger, exception.Provider, (int)exception.StatusCode);

            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cover art lookup against {Provider} failed with status {StatusCode}")]
    private static partial void LogProviderFailure(ILogger logger, string provider, int statusCode);
}
