using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Songs;

/// <summary>What MusicBrainz knows about the song's recording.</summary>
/// <param name="RecordingId">The recording MBID.</param>
/// <param name="FirstReleaseDate">The recording's first release date, or <see langword="null"/>.</param>
/// <param name="Disambiguation">The MusicBrainz disambiguation comment, or <see langword="null"/>.</param>
/// <param name="Isrcs">The ISRCs the recording is registered under.</param>
/// <param name="ArtistCredit">The display credit.</param>
public sealed record SongMusicBrainzDetails(
    string RecordingId,
    string? FirstReleaseDate,
    string? Disambiguation,
    IReadOnlyList<string> Isrcs,
    string ArtistCredit);

/// <summary>What Deezer knows about the song's track.</summary>
/// <param name="TrackId">The Deezer track id.</param>
/// <param name="Rank">Deezer's popularity rank, or <see langword="null"/>.</param>
/// <param name="ExplicitLyrics">Whether Deezer flags the lyrics as explicit.</param>
/// <param name="Bpm">The tempo in beats per minute, or <see langword="null"/> when Deezer has not analysed it.</param>
/// <param name="Gain">Deezer's loudness gain in dB, or <see langword="null"/> when it has not analysed it.</param>
/// <param name="ReleaseDate">The release date, or <see langword="null"/>.</param>
/// <param name="AlbumCoverUrl">The album cover URL, or <see langword="null"/>.</param>
public sealed record SongDeezerDetails(
    long TrackId,
    long? Rank,
    bool ExplicitLyrics,
    double? Bpm,
    double? Gain,
    string? ReleaseDate,
    string? AlbumCoverUrl);

/// <summary>The reference-library row a song is owned through.</summary>
/// <param name="LibraryId">The reference library.</param>
/// <param name="LibraryName">The library's name.</param>
/// <param name="RelativePath">The file's path relative to the library root.</param>
/// <param name="IdentifiedBy">How the file was identified, or <see langword="null"/>.</param>
/// <param name="Confidence">How sure the identification was, from 0 to 1.</param>
/// <param name="State">What the last scan concluded.</param>
public sealed record SongReferenceFileDetails(
    long LibraryId,
    string LibraryName,
    string RelativePath,
    string? IdentifiedBy,
    double Confidence,
    ReferenceFileState State);

/// <summary>Which lyrics sidecars sit next to the song's file.</summary>
/// <param name="Source"><c>sidecar</c> when at least one exists, otherwise <c>none</c>.</param>
/// <param name="Synced">Whether a <c>.lrc</c> sidecar exists.</param>
/// <param name="Plain">Whether a <c>.txt</c> sidecar exists.</param>
public sealed record SongLyricsAvailability(string Source, bool Synced, bool Plain);

/// <summary>
/// Everything the song page shows that the song itself does not carry. Each external part is
/// <see langword="null"/> when its source is missing, failed or was too slow.
/// </summary>
/// <param name="Releases">The song's cached release options; empty when none are known.</param>
/// <param name="MusicBrainz">The MusicBrainz recording, or <see langword="null"/>.</param>
/// <param name="Deezer">The Deezer track, or <see langword="null"/>.</param>
/// <param name="ReferenceFile">The reference-library row, or <see langword="null"/>.</param>
/// <param name="Lyrics">The lyrics sidecars next to the file.</param>
/// <param name="CurrentAlbumKey">The album key the song is filed under, or <see langword="null"/>.</param>
public sealed record SongDetails(
    IReadOnlyList<ReleaseOption> Releases,
    SongMusicBrainzDetails? MusicBrainz,
    SongDeezerDetails? Deezer,
    SongReferenceFileDetails? ReferenceFile,
    SongLyricsAvailability Lyrics,
    string? CurrentAlbumKey);

/// <summary>Assembles the data behind a song's own page.</summary>
public interface ISongDetailsService
{
    /// <summary>Gets the details of one song.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The details, or <see langword="null"/> when the song does not exist.</returns>
    Task<SongDetails?> GetAsync(long songId, CancellationToken cancellationToken);

    /// <summary>Reads one reference-library row, library name included.</summary>
    /// <param name="referenceFileId">The reference-library file row.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The row, or <see langword="null"/> when it no longer exists.</returns>
    Task<SongReferenceFileDetails?> GetReferenceFileAsync(long referenceFileId, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="ISongDetailsService"/>. The song's own rows come from the database; the
/// recording and the release options come from the stored identity (the metadata cache, so a repeat
/// costs nothing and a miss is one MusicBrainz call through the rate-limited client); Deezer is asked
/// once per hour per track. Every external call is bounded by <see cref="SourceTimeout"/> and a
/// failure leaves just its own section empty. Nothing here writes a file or a row.
/// </summary>
public sealed partial class SongDetailsService : ISongDetailsService
{
    /// <summary>How long one external source may take before its section is left empty.</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a Deezer answer is kept in memory.</summary>
    public static readonly TimeSpan DeezerTtl = TimeSpan.FromHours(1);

    private readonly WondarrDbContext _database;
    private readonly IIdentityResolver _resolver;
    private readonly IDeezerClient _deezer;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SongDetailsService> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongDetailsService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="resolver">The identity resolver, for the recording and the release options.</param>
    /// <param name="deezer">The Deezer client.</param>
    /// <param name="cache">The in-memory cache the Deezer answers live in.</param>
    /// <param name="logger">The logger.</param>
    public SongDetailsService(
        WondarrDbContext database,
        IIdentityResolver resolver,
        IDeezerClient deezer,
        IMemoryCache cache,
        ILogger<SongDetailsService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(deezer);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _resolver = resolver;
        _deezer = deezer;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongDetails?> GetAsync(long songId, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .AsNoTracking()
            .Include(candidate => candidate.AlbumContext)
            .Include(candidate => candidate.File)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        var identity = await IdentityAsync(song, cancellationToken).ConfigureAwait(false);
        var deezer = await DeezerAsync(song, cancellationToken).ConfigureAwait(false);
        var reference = await ReferenceAsync(song, cancellationToken).ConfigureAwait(false);

        return new SongDetails(
            identity?.ReleaseOptions ?? [],
            MusicBrainzOf(song, identity),
            deezer,
            reference,
            LyricsOf(song),
            song.AlbumContext?.AlbumKey);
    }

    /// <inheritdoc />
    public async Task<SongReferenceFileDetails?> GetReferenceFileAsync(
        long referenceFileId,
        CancellationToken cancellationToken) =>
        await _database.ReferenceFiles
            .AsNoTracking()
            .Where(file => file.Id == referenceFileId)
            .Select(file => new SongReferenceFileDetails(
                file.ReferenceLibraryId,
                file.ReferenceLibrary.Name,
                file.RelativePath,
                file.IdentifiedBy,
                file.Confidence,
                file.State))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The song's stored identity, or <see langword="null"/> when it has none or the source failed.</summary>
    private Task<SongIdentity?> IdentityAsync(Song song, CancellationToken cancellationToken)
    {
        if (song.MbRecordingId is null && song.DeezerId is null)
        {
            return Task.FromResult<SongIdentity?>(null);
        }

        return BoundedAsync(
            "identity",
            token => _resolver.GetIdentityAsync(song.MbRecordingId, song.DeezerId, token),
            cancellationToken);
    }

    /// <summary>The MusicBrainz section, built from the identity when it came from MusicBrainz.</summary>
    private static SongMusicBrainzDetails? MusicBrainzOf(Song song, SongIdentity? identity)
    {
        if (song.MbRecordingId is null)
        {
            return null;
        }

        var stored = identity is not null && string.Equals(identity.Source, "musicbrainz", StringComparison.OrdinalIgnoreCase)
            ? identity
            : null;

        return new SongMusicBrainzDetails(
            song.MbRecordingId,
            stored?.OriginalDate,
            stored?.Disambiguation,
            song.Isrcs.Count > 0 ? song.Isrcs : stored?.Isrcs ?? [],
            song.ArtistCredit);
    }

    /// <summary>The Deezer section: from memory when fresh, otherwise one call.</summary>
    private async Task<SongDeezerDetails?> DeezerAsync(Song song, CancellationToken cancellationToken)
    {
        if (song.DeezerId is not { } id)
        {
            return null;
        }

        var key = $"song-details:deezer:{id}";

        if (_cache.TryGetValue(key, out SongDeezerDetails? cached))
        {
            return cached;
        }

        var fetched = await BoundedAsync(
            "deezer",
            async token => await _deezer.GetTrackAsync(id, token).ConfigureAwait(false) is { } track
                ? new Fetched<SongDeezerDetails>(Map(track))
                : new Fetched<SongDeezerDetails>(null),
            cancellationToken).ConfigureAwait(false);

        if (fetched is null)
        {
            // Failed or too slow: not remembered, the next page view tries again.
            return null;
        }

        _cache.Set(key, fetched.Value, DeezerTtl);

        return fetched.Value;
    }

    private static SongDeezerDetails Map(DeezerTrack track) =>
        new(
            track.Id,
            track.Rank,
            track.ExplicitLyrics,
            track.Bpm is > 0 ? track.Bpm : null,
            track.Gain is not null and not 0 ? track.Gain : null,
            string.IsNullOrWhiteSpace(track.ReleaseDate) ? null : track.ReleaseDate,
            string.IsNullOrWhiteSpace(track.Album.CoverXl) ? null : track.Album.CoverXl);

    /// <summary>The reference-library row of a song that is owned through one, read only.</summary>
    private async Task<SongReferenceFileDetails?> ReferenceAsync(Song song, CancellationToken cancellationToken)
    {
        if (song.File is not { SourceType: SourceTypes.Reference } file)
        {
            return null;
        }

        var source = SongFileSource.Parse(file.SourceType, file.SourceRef);

        return source.ReferenceFileId is { } id
            ? await GetReferenceFileAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Looks for the sidecars next to the file; the lyrics text is never read here.</summary>
    private static SongLyricsAvailability LyricsOf(Song song)
    {
        if (song.File is null || string.IsNullOrWhiteSpace(song.File.Path))
        {
            return new SongLyricsAvailability("none", false, false);
        }

        var synced = SongLyricsService.SidecarExists(song.File.Path, LyricsSidecar.SyncedExtension);
        var plain = SongLyricsService.SidecarExists(song.File.Path, LyricsSidecar.PlainExtension);

        return new SongLyricsAvailability(synced || plain ? "sidecar" : "none", synced, plain);
    }

    /// <summary>
    /// Runs one external call under <see cref="SourceTimeout"/>. A timeout or a failure is logged and
    /// answers <see langword="null"/>; only the caller's own cancellation propagates.
    /// </summary>
    private async Task<T?> BoundedAsync<T>(
        string source,
        Func<CancellationToken, Task<T?>> work,
        CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceTimeout);

        try
        {
            return await work(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(_logger, source);

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(_logger, source, exception.Message);

            return null;
        }
    }

    /// <summary>Tells "Deezer answered, and it does not know the track" from "Deezer could not be asked".</summary>
    private sealed record Fetched<T>(T? Value)
        where T : class;

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Source} part of a song's details timed out and is left out.")]
    private static partial void LogTimedOut(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Source} part of a song's details failed and is left out: {Reason}")]
    private static partial void LogFailed(ILogger logger, string source, string reason);
}
