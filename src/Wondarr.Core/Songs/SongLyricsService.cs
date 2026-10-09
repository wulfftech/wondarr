using System.Globalization;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Songs;

/// <summary>A song's lyrics text and where it came from.</summary>
/// <param name="Source"><c>sidecar</c> (a file next to the song), <c>lrclib</c> (a lookup), or <see langword="null"/> when there are none.</param>
/// <param name="Synced">The LRC text with its time tags, or <see langword="null"/>.</param>
/// <param name="Plain">The unsynced text, or <see langword="null"/>.</param>
public sealed record SongLyricsText(string? Source, string? Synced, string? Plain)
{
    /// <summary>No lyrics anywhere.</summary>
    public static SongLyricsText None { get; } = new(null, null, null);
}

/// <summary>Reads a song's lyrics for the song page. Strictly read only: no sidecar is ever written here.</summary>
public interface ISongLyricsService
{
    /// <summary>Gets the lyrics of one song.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The lyrics, or <see langword="null"/> when the song does not exist.</returns>
    Task<SongLyricsText?> GetAsync(long songId, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="ISongLyricsService"/>: the sidecar next to the song's file when there is
/// one, otherwise one LRCLIB lookup (bounded by <see cref="SongDetailsService.SourceTimeout"/>)
/// remembered in memory for a day. Writing the sidecar stays the import's job.
/// </summary>
public sealed partial class SongLyricsService : ISongLyricsService
{
    /// <summary>How long an LRCLIB answer is kept in memory.</summary>
    public static readonly TimeSpan LookupTtl = TimeSpan.FromDays(1);

    /// <summary>The biggest sidecar that is read; a lyrics file is a few kilobytes, anything bigger is not one.</summary>
    /// <summary>How long an unavailable (throttled, failed, timed-out) lookup is remembered, so a struggling source is not retried on every page view.</summary>
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromMinutes(1);

    private const long MaxSidecarBytes = 1024 * 1024;

    private readonly WondarrDbContext _database;
    private readonly ILrclibClient _lrclib;
    private readonly IOptionsMonitor<LyricsOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SongLyricsService> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongLyricsService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="lrclib">The LRCLIB client.</param>
    /// <param name="options">The lyrics settings, for <c>lyrics.enabled</c>.</param>
    /// <param name="cache">The in-memory cache LRCLIB answers live in.</param>
    /// <param name="logger">The logger.</param>
    public SongLyricsService(
        WondarrDbContext database,
        ILrclibClient lrclib,
        IOptionsMonitor<LyricsOptions> options,
        IMemoryCache cache,
        ILogger<SongLyricsService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(lrclib);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _lrclib = lrclib;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Whether the sidecar with this extension sits next to the audio file.</summary>
    /// <param name="audioPath">The audio file.</param>
    /// <param name="extension">The sidecar's extension, with its dot.</param>
    internal static bool SidecarExists(string audioPath, string extension)
    {
        try
        {
            return File.Exists(Path.ChangeExtension(audioPath, extension));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SongLyricsText?> GetAsync(long songId, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .AsNoTracking()
            .Include(candidate => candidate.PrimaryArtist)
            .Include(candidate => candidate.File)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        if (song.File is { } file && !string.IsNullOrWhiteSpace(file.Path))
        {
            var sidecar = await ReadSidecarAsync(file.Path, cancellationToken).ConfigureAwait(false);

            if (sidecar is not null)
            {
                return sidecar;
            }
        }

        // Switched off only stops the outside lookup: a sidecar the user already has is still theirs to read.
        if (!_options.CurrentValue.Enabled)
        {
            return SongLyricsText.None;
        }

        var seconds = DurationSeconds(song.File?.DurationMs ?? song.DurationMs);
        var artist = string.IsNullOrWhiteSpace(song.PrimaryArtist?.Name) ? song.ArtistCredit : song.PrimaryArtist!.Name;

        // The length is what makes a lookup a match rather than a guess, exactly as at import.
        if (seconds is null || string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(artist))
        {
            return SongLyricsText.None;
        }

        return await LookUpAsync(song.Title, artist, seconds.Value, cancellationToken).ConfigureAwait(false);
    }

    private static int? DurationSeconds(int? durationMs) =>
        durationMs is > 0 ? (int)Math.Round(durationMs.Value / 1000.0) : null;

    /// <summary>Reads the sidecars next to the file; <see langword="null"/> when there is none.</summary>
    private async Task<SongLyricsText?> ReadSidecarAsync(string audioPath, CancellationToken cancellationToken)
    {
        var synced = await ReadTextAsync(audioPath, LyricsSidecar.SyncedExtension, cancellationToken)
            .ConfigureAwait(false);
        var plain = await ReadTextAsync(audioPath, LyricsSidecar.PlainExtension, cancellationToken)
            .ConfigureAwait(false);

        if (synced is null && plain is null)
        {
            return null;
        }

        // A synced-only sidecar still has plain text: it is the same lines without their time tags.
        plain ??= LrclibClient.DerivePlainLyrics(synced!) is { Length: > 0 } derived ? derived : null;

        return new SongLyricsText("sidecar", synced, plain);
    }

    private async Task<string?> ReadTextAsync(string audioPath, string extension, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.ChangeExtension(audioPath, extension);
            var info = new FileInfo(path);

            if (!info.Exists || info.Length > MaxSidecarBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            LogSidecarUnreadable(_logger, exception.Message);

            return null;
        }
    }

    /// <summary>One LRCLIB lookup, remembered; a failed or timed-out one is not.</summary>
    private async Task<SongLyricsText> LookUpAsync(
        string title,
        string artist,
        int seconds,
        CancellationToken cancellationToken)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"song-lyrics:{artist.ToUpperInvariant()}|{title.ToUpperInvariant()}|{seconds}");

        if (_cache.TryGetValue(key, out SongLyricsText? cached) && cached is not null)
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SongDetailsService.SourceTimeout);

        LyricsLookup lookup;

        try
        {
            lookup = await _lrclib.FindAsync(title, artist, seconds, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogLookupFailed(_logger, "timed out");
            _cache.Set(key, SongLyricsText.None, UnavailableTtl);

            return SongLyricsText.None;
        }

        if (lookup.Status == LyricsLookupStatus.Unavailable)
        {
            _cache.Set(key, SongLyricsText.None, UnavailableTtl);

            return SongLyricsText.None;
        }

        var result = lookup.Status == LyricsLookupStatus.Found
            ? new SongLyricsText("lrclib", lookup.SyncedLyrics, lookup.PlainLyrics)
            : SongLyricsText.None;

        _cache.Set(key, result, LookupTtl);

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A lyrics sidecar could not be read: {Reason}")]
    private static partial void LogSidecarUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The LRCLIB lookup for a song page failed: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string reason);
}
