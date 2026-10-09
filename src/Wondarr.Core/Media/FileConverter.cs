using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Media;

/// <summary>Which files a conversion covers, and by what rule.</summary>
/// <param name="SongIds">The songs to convert, or <see langword="null"/> when a whole library is named.</param>
/// <param name="LibraryId">The library whose songs are converted, or <see langword="null"/> when songs are named.</param>
/// <param name="RuleJson">
/// A one-off rule (the version-1 rule shape, for example <c>{"codec":"mp3","bitrateKbps":320}</c>), or
/// <see langword="null"/> for each song's library policy.
/// </param>
public sealed record ConvertRequest(IReadOnlyList<long>? SongIds, long? LibraryId, string? RuleJson);

/// <summary>What a conversion would do, or did, to one song.</summary>
public enum ConvertOutcome
{
    /// <summary>The file is (or would be) converted.</summary>
    Converted,

    /// <summary>Nothing to do: no file, a reference file, a keep rule, or already in the target codec.</summary>
    Skipped,

    /// <summary>The rule cannot apply to this file (a lossless target for a lossy file), or the song is busy.</summary>
    Refused,

    /// <summary>The conversion was tried and did not work; the original is where it was.</summary>
    Failed,
}

/// <summary>One song's conversion, planned or done.</summary>
/// <param name="SongId">The song.</param>
/// <param name="Outcome">What happens, or happened.</param>
/// <param name="Reason">Why, for anything but a conversion.</param>
/// <param name="FromCodec">The current codec, when the song has a file.</param>
/// <param name="ToCodec">The target codec, when the rule converts.</param>
/// <param name="CurrentSize">The current file's size in bytes.</param>
/// <param name="EstimatedSize">The converted file's size, estimated (a plan) or measured (done).</param>
public sealed record ConvertSongResult(
    long SongId,
    ConvertOutcome Outcome,
    string? Reason,
    string? FromCodec = null,
    string? ToCodec = null,
    long CurrentSize = 0,
    long EstimatedSize = 0);

/// <summary>A dry run over many songs.</summary>
/// <param name="Convert">How many songs would be converted.</param>
/// <param name="Skip">How many would be left as they are.</param>
/// <param name="Refuse">How many the rule cannot apply to.</param>
/// <param name="CurrentSize">The total size of the files that would be converted.</param>
/// <param name="EstimatedSize">Their estimated total size after the conversion.</param>
/// <param name="Songs">The first rows, in song order.</param>
public sealed record ConvertPlan(
    int Convert,
    int Skip,
    int Refuse,
    long CurrentSize,
    long EstimatedSize,
    IReadOnlyList<ConvertSongResult> Songs);

/// <summary>A request the converter cannot run, naming the field at fault.</summary>
public sealed class ConvertRequestException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ConvertRequestException"/> class.</summary>
    /// <param name="field">The field at fault, as the API names it.</param>
    /// <param name="detail">What is wrong with it.</param>
    public ConvertRequestException(string field, string detail)
        : base($"{field}: {detail}")
    {
        Field = field;
        Detail = detail;
    }

    /// <summary>Gets the field at fault.</summary>
    public string Field { get; }

    /// <summary>Gets what is wrong with it.</summary>
    public string Detail { get; }
}

/// <summary>Converts files already in the library (DECISIONS build session 7 #6).</summary>
public interface IFileConverter
{
    /// <summary>Resolves a request to the songs it covers, checking it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="ConvertRequestException">The request names nothing, or too much, or a rule that does not parse.</exception>
    Task<IReadOnlyList<long>> ResolveSongsAsync(ConvertRequest request, CancellationToken cancellationToken);

    /// <summary>The dry run, from the database alone: no file is probed or touched.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<ConvertPlan> PlanAsync(ConvertRequest request, CancellationToken cancellationToken);

    /// <summary>Converts one song's file.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="ruleJson">A one-off rule, or <see langword="null"/> for the library's policy.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    Task<ConvertSongResult> ConvertAsync(long songId, string? ruleJson, CancellationToken cancellationToken);
}

/// <summary>
/// Converts a song's file in place, with an upgrade's safety rules: inside the per-song lock, to a
/// temporary file in a hidden folder under the library root, checked (it decodes and is as long as
/// the original), then tagged and placed by the import's organizer, which recycles the original. The
/// song keeps the quality it was downloaded at (DECISIONS build session 7 #5); a reference file — the
/// user's own — is never converted.
/// </summary>
public sealed partial class FileConverter : IFileConverter
{
    /// <summary>The most songs one request covers.</summary>
    public const int MaxSongs = 10_000;

    /// <summary>The hidden folder under a library root the converted files are written to first.</summary>
    public const string WorkFolderName = ".wondarr-convert";

    /// <summary>How many rows a plan lists.</summary>
    private const int PlanRows = 200;

    /// <summary>How far the converted file's length may be from the original's, in milliseconds.</summary>
    private const int DurationToleranceMs = 1_000;

    private static readonly string[] SidecarExtensions = [".lrc", ".txt"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WondarrDbContext _database;
    private readonly ITranscoder _transcoder;
    private readonly IMediaProbe _probe;
    private readonly ILibraryOrganizer _organizer;
    private readonly IReplayGainAnalyzer _replayGain;
    private readonly ISongFileLock _songFileLock;
    private readonly IPlexLibraryUpdater _updater;
    private readonly ILogger<FileConverter> _logger;

    /// <summary>Initialises a new instance of the <see cref="FileConverter"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="transcoder">Writes the converted file.</param>
    /// <param name="probe">Measures the current and the converted file.</param>
    /// <param name="organizer">Tags and places the converted file, recycling the original.</param>
    /// <param name="replayGain">Measures the converted file again when the library has ReplayGain on.</param>
    /// <param name="songFileLock">The per-song lock imports and compaction take.</param>
    /// <param name="updater">Asks Plex to scan the folder.</param>
    /// <param name="logger">The logger.</param>
    public FileConverter(
        WondarrDbContext database,
        ITranscoder transcoder,
        IMediaProbe probe,
        ILibraryOrganizer organizer,
        IReplayGainAnalyzer replayGain,
        ISongFileLock songFileLock,
        IPlexLibraryUpdater updater,
        ILogger<FileConverter> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transcoder);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(replayGain);
        ArgumentNullException.ThrowIfNull(songFileLock);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _transcoder = transcoder;
        _probe = probe;
        _organizer = organizer;
        _replayGain = replayGain;
        _songFileLock = songFileLock;
        _updater = updater;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> ResolveSongsAsync(ConvertRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Parsed only to be checked: a rule that does not parse is the request's fault, said up front.
        _ = ParseRule(request.RuleJson);

        if ((request.SongIds is { Count: > 0 }) == request.LibraryId is not null)
        {
            throw new ConvertRequestException("songIds", "Name either songIds or libraryId, not both and not neither.");
        }

        if (request.SongIds is { } songIds)
        {
            if (songIds.Count > MaxSongs)
            {
                throw new ConvertRequestException("songIds", $"At most {MaxSongs} songs per conversion.");
            }

            return [.. songIds.Distinct()];
        }

        var libraryId = request.LibraryId!.Value;

        if (!await _database.Libraries.AnyAsync(library => library.Id == libraryId, cancellationToken).ConfigureAwait(false))
        {
            throw new ConvertRequestException(
                "libraryId",
                $"Library {libraryId.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        var ids = await _database.Songs
            .AsNoTracking()
            .Where(song => song.LibraryId == libraryId && song.File != null)
            .OrderBy(song => song.Id)
            .Select(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids.Count > MaxSongs
            ? throw new ConvertRequestException("libraryId", $"The library holds more than {MaxSongs} songs with files; convert it in parts.")
            : ids;
    }

    /// <inheritdoc />
    public async Task<ConvertPlan> PlanAsync(ConvertRequest request, CancellationToken cancellationToken)
    {
        var ids = await ResolveSongsAsync(request, cancellationToken).ConfigureAwait(false);
        var oneOff = ParseRule(request.RuleJson);

        var query = _database.Songs
            .AsNoTracking()
            .Include(song => song.File).ThenInclude(file => file!.Quality)
            .AsQueryable();

        // A whole library is read by its id, not by a list of thousands of song ids.
        query = request.LibraryId is { } libraryId
            ? query.Where(song => song.LibraryId == libraryId && song.File != null)
            : query.Where(song => ids.Contains(song.Id));

        var songs = await query
            .OrderBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var policies = await _database.Libraries
            .AsNoTracking()
            .ToDictionaryAsync(library => library.Id, library => library.OutputPolicy, cancellationToken)
            .ConfigureAwait(false);

        var parsed = new Dictionary<long, LibraryOutputPolicy>();
        var rows = new List<ConvertSongResult>(songs.Count);

        foreach (var song in songs)
        {
            if (!parsed.TryGetValue(song.LibraryId, out var policy))
            {
                policy = LibraryOutputPolicy.Parse(policies.GetValueOrDefault(song.LibraryId));
                parsed[song.LibraryId] = policy;
            }

            rows.Add(Plan(song, oneOff ?? Rule(policy, song.File)));
        }

        var converting = rows.Where(row => row.Outcome == ConvertOutcome.Converted).ToList();

        return new ConvertPlan(
            converting.Count,
            rows.Count(row => row.Outcome == ConvertOutcome.Skipped),
            rows.Count(row => row.Outcome == ConvertOutcome.Refused),
            converting.Sum(row => row.CurrentSize),
            converting.Sum(row => row.EstimatedSize),
            [.. rows.Take(PlanRows)]);
    }

    /// <inheritdoc />
    public async Task<ConvertSongResult> ConvertAsync(long songId, string? ruleJson, CancellationToken cancellationToken)
    {
        var oneOff = ParseRule(ruleJson);

        var song = await LoadSongAsync(songId, cancellationToken).ConfigureAwait(false);

        if (song is null)
        {
            return new ConvertSongResult(songId, ConvertOutcome.Skipped, "The song does not exist.");
        }

        if (song.File is not { } file)
        {
            return new ConvertSongResult(songId, ConvertOutcome.Skipped, "The song has no file.");
        }

        if (string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal))
        {
            return new ConvertSongResult(songId, ConvertOutcome.Skipped, "A reference file is the user's own and is never converted.");
        }

        var library = await _database.Libraries
            .FirstAsync(candidate => candidate.Id == song.LibraryId, cancellationToken)
            .ConfigureAwait(false);

        await using var songFileLock = await _songFileLock.AcquireAsync(song.Id, cancellationToken).ConfigureAwait(false);

        if (await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == song.Id, cancellationToken)
                .ConfigureAwait(false)
            || await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(item => item.SongId == song.Id && item.State == QueueItemState.Importing, cancellationToken)
                .ConfigureAwait(false))
        {
            return new ConvertSongResult(songId, ConvertOutcome.Refused, "The song's file is being moved or imported; try again later.", file.Codec);
        }

        var currentPath = file.Path;
        var probed = await _probe.ProbeAsync(currentPath, cancellationToken).ConfigureAwait(false);

        if (!probed.Decodable || probed.Info is not { } info)
        {
            return new ConvertSongResult(songId, ConvertOutcome.Failed, "The current file does not decode.", file.Codec);
        }

        var rule = oneOff ?? LibraryOutputPolicy.Parse(library.OutputPolicy).RuleFor(file.SourceType, info.IsLossless);

        if (rule.Codec == OutputCodec.Keep || LibraryOutputPolicy.SameCodec(rule, info.Codec))
        {
            return new ConvertSongResult(songId, ConvertOutcome.Skipped, KeepReason(rule), info.Codec);
        }

        var workFolder = Path.Combine(library.RootPath, WorkFolderName);
        var tempPath = Path.Combine(
            workFolder,
            string.Concat(song.Id.ToString(CultureInfo.InvariantCulture), ".", rule.Container));
        var placed = false;

        try
        {
            Directory.CreateDirectory(workFolder);
            DeleteQuietly(tempPath);

            try
            {
                await _transcoder
                    .TranscodeAsync(currentPath, rule, tempPath, info.IsLossless, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TranscodePolicyException exception)
            {
                return new ConvertSongResult(songId, ConvertOutcome.Refused, exception.Message, info.Codec, CodecName(rule));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(_logger, songId, exception);

                return new ConvertSongResult(songId, ConvertOutcome.Failed, $"The conversion failed: {exception.Message}", info.Codec, CodecName(rule));
            }

            var converted = await _probe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);

            if (!converted.Decodable || converted.Info is not { } media)
            {
                return new ConvertSongResult(songId, ConvertOutcome.Failed, "The converted file does not decode.", info.Codec, CodecName(rule));
            }

            if (Math.Abs(media.DurationMs - info.DurationMs) > DurationToleranceMs)
            {
                return new ConvertSongResult(
                    songId,
                    ConvertOutcome.Failed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The converted file is {media.DurationMs / 1000.0:0.#} s long; the original is {info.DurationMs / 1000.0:0.#} s."),
                    info.Codec,
                    CodecName(rule));
            }

            var album = song.AlbumContext;

            if (album is null)
            {
                return new ConvertSongResult(songId, ConvertOutcome.Failed, "The song has no album context to file it under.", info.Codec);
            }

            var credits = song.Artists
                .OrderBy(credit => credit.Position)
                .Select(credit => (credit.Artist, credit.Role))
                .ToList();

            // Encoding changes the peak (and a little of the loudness), so the converted file is
            // measured again; a failed measurement leaves it without the tags, never fails the conversion.
            var replayGain = library.ReplayGain
                ? await _replayGain.MeasureAsync(tempPath, cancellationToken).ConfigureAwait(false)
                : null;

            OrganizeResult placement;

            try
            {
                placement = await _organizer
                    .OrganizeAsync(
                        new OrganizeRequest(
                            song,
                            album,
                            credits,
                            library,
                            tempPath,
                            rule.Container,
                            media,

                            // The song keeps the quality it was downloaded at (#5): the conversion
                            // changes the file, never what the file is ranked as.
                            file.Quality,
                            file.SourceType,
                            file.AcoustId,
                            KeepSource: false,

                            // The organizer's placer recycles the original before it places the new
                            // file, and puts it back if the placing fails: the upgrade path.
                            ReplacesPath: currentPath,
                            LookUpLyrics: false,
                            ReplayGainDb: replayGain?.GainDb,
                            ReplayGainPeak: replayGain?.Peak),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _database.ChangeTracker.Clear();
                LogFailed(_logger, songId, exception);

                return new ConvertSongResult(songId, ConvertOutcome.Failed, $"Placing the converted file failed: {exception.Message}", info.Codec, CodecName(rule));
            }

            if (!placement.Success || placement.FinalPath is not { } finalPath)
            {
                _database.ChangeTracker.Clear();

                return new ConvertSongResult(
                    songId,
                    ConvertOutcome.Failed,
                    placement.Error ?? "The organizer gave no final path.",
                    info.Codec,
                    CodecName(rule));
            }

            placed = true;

            MoveSidecars(currentPath, finalPath);

            var from = new ConvertedFile(file.Codec, file.BitrateKbps, currentPath);

            file.Path = finalPath;
            file.Size = media.SizeBytes > 0 ? media.SizeBytes : new FileInfo(finalPath).Length;
            file.Codec = media.Codec;
            file.Container = media.Container;
            file.BitrateKbps = media.BitrateKbps;
            file.SampleRate = media.SampleRate;
            file.BitDepth = media.BitDepth;
            file.Channels = media.Channels;
            file.DurationMs = media.DurationMs;
            file.TagsWritten = JsonSerializer.Serialize(placement.TagsWritten, Json);
            file.ReplayGainDb = replayGain?.GainDb;
            file.ReplayGainPeak = replayGain?.Peak;

            _database.History.Add(new HistoryItem
            {
                SongId = song.Id,
                EventType = HistoryEventType.Converted,
                Data = JsonSerializer.Serialize(
                    new ConvertHistoryData(from, new ConvertedFile(media.Codec, media.BitrateKbps, finalPath)),
                    Json),
            });

            try
            {
                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The new file is in the library and the original in the recycle bin; only the
                // bookkeeping failed. The least the row must say is where the song's file is now, so
                // it is written on its own (as the compaction executor does); the rest is logged.
                LogFailed(_logger, songId, exception);
                _database.ChangeTracker.Clear();

                var fileId = file.Id;
                await _database.SongFiles
                    .Where(candidate => candidate.Id == fileId)
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(candidate => candidate.Path, finalPath)
                            .SetProperty(candidate => candidate.Codec, media.Codec)
                            .SetProperty(candidate => candidate.Container, media.Container),
                        cancellationToken)
                    .ConfigureAwait(false);

                return new ConvertSongResult(
                    songId,
                    ConvertOutcome.Failed,
                    $"Converted to {finalPath}, but recording it failed: {exception.Message}",
                    info.Codec,
                    media.Codec);
            }

            var newFolder = Path.GetDirectoryName(finalPath) ?? library.RootPath;
            var oldFolder = Path.GetDirectoryName(currentPath) ?? library.RootPath;

            _updater.RequestFolder(library.Id, newFolder);

            if (!string.Equals(oldFolder, newFolder, StringComparison.Ordinal))
            {
                _updater.RequestFolder(library.Id, oldFolder);
            }

            LogConverted(_logger, songId, info.Codec, media.Codec, finalPath);

            return new ConvertSongResult(songId, ConvertOutcome.Converted, null, info.Codec, media.Codec, info.SizeBytes, file.Size);
        }
        finally
        {
            if (!placed)
            {
                DeleteQuietly(tempPath);
            }
        }
    }

    private static OutputPolicy? ParseRule(string? ruleJson)
    {
        if (string.IsNullOrWhiteSpace(ruleJson))
        {
            return null;
        }

        try
        {
            // A lossless target is allowed here: the transcoder still refuses it, file by file, for a
            // lossy source.
            return OutputPolicy.Parse(ruleJson, allowLossless: true);
        }
        catch (ProfileValidationException exception)
        {
            throw new ConvertRequestException(
                "rule",
                string.Join(" ", exception.Errors.Select(error => error.Message)));
        }
    }

    private static OutputPolicy Rule(LibraryOutputPolicy policy, SongFile? file) =>
        file is null
            ? policy.Lossy
            : policy.RuleFor(file.SourceType, file.Quality?.Lossless ?? false);

    /// <summary>One dry-run row, from the stored file row alone.</summary>
    private static ConvertSongResult Plan(Song song, OutputPolicy rule)
    {
        if (song.File is not { } file)
        {
            return new ConvertSongResult(song.Id, ConvertOutcome.Skipped, "The song has no file.");
        }

        if (string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal))
        {
            return new ConvertSongResult(song.Id, ConvertOutcome.Skipped, "A reference file is never converted.", file.Codec, null, file.Size);
        }

        if (rule.Codec == OutputCodec.Keep || LibraryOutputPolicy.SameCodec(rule, file.Codec))
        {
            return new ConvertSongResult(song.Id, ConvertOutcome.Skipped, KeepReason(rule), file.Codec, null, file.Size);
        }

        var lossless = file.Quality?.Lossless ?? false;

        if (rule.Codec is OutputCodec.Flac or OutputCodec.Alac && !lossless)
        {
            return new ConvertSongResult(
                song.Id,
                ConvertOutcome.Refused,
                "A lossy file is never converted to a lossless format.",
                file.Codec,
                CodecName(rule),
                file.Size);
        }

        return new ConvertSongResult(song.Id, ConvertOutcome.Converted, null, file.Codec, CodecName(rule), file.Size, Estimate(rule, file));
    }

    /// <summary>The size a conversion would write: bitrate × length, or today's size for a lossless target.</summary>
    private static long Estimate(OutputPolicy rule, SongFile file)
    {
        if (rule.Codec is OutputCodec.Flac or OutputCodec.Alac || file.DurationMs is not { } durationMs)
        {
            return file.Size;
        }

        var kbps = rule.Mode == OutputMode.Vbr
            ? VbrKbps(rule.VbrQuality)
            : rule.BitrateKbps;

        return (long)kbps * 125 * durationMs / 1000;
    }

    /// <summary>LAME's typical average bitrate per VBR quality level (V0 ≈ 245 kbps … V9 ≈ 65 kbps).</summary>
    private static int VbrKbps(int quality) => quality switch
    {
        0 => 245,
        1 => 225,
        2 => 190,
        3 => 175,
        4 => 165,
        5 => 130,
        6 => 115,
        7 => 100,
        8 => 85,
        _ => 65,
    };

    private static string KeepReason(OutputPolicy rule) =>
        rule.Codec == OutputCodec.Keep
            ? "The rule keeps this file as it is."
            : $"The file is already {CodecName(rule)}.";

    private static string CodecName(OutputPolicy rule) => rule.Codec switch
    {
        OutputCodec.Aac => "aac",
        OutputCodec.Mp3 => "mp3",
        OutputCodec.Opus => "opus",
        OutputCodec.Flac => "flac",
        OutputCodec.Alac => "alac",
        _ => "keep",
    };

    /// <summary>
    /// Moves the sidecars of the old file beside the new one when the name changed. A sidecar whose
    /// new name is taken stays where it was: nothing is ever replaced.
    /// </summary>
    private static void MoveSidecars(string oldPath, string newPath)
    {
        var oldBase = Path.ChangeExtension(oldPath, null);
        var newBase = Path.ChangeExtension(newPath, null);

        if (oldBase is null || newBase is null || string.Equals(oldBase, newBase, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var extension in SidecarExtensions)
        {
            var from = string.Concat(oldBase, extension);
            var to = string.Concat(newBase, extension);

            if (!File.Exists(from) || File.Exists(to))
            {
                continue;
            }

            try
            {
                File.Move(from, to);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The sidecar is the user's lyrics; leaving it beside the recycled name is better
                // than failing a conversion that already happened.
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next attempt deletes it before it writes its own.
        }
    }

    private async Task<Song?> LoadSongAsync(long songId, CancellationToken cancellationToken) =>
        await _database.Songs
            .Include(song => song.AlbumContext)
            .Include(song => song.File).ThenInclude(file => file!.Quality)
            .Include(song => song.Artists).ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Information, Message = "Converted song {SongId} from {From} to {To}: {Path}")]
    private static partial void LogConverted(ILogger logger, long songId, string from, string to, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Converting song {SongId} failed")]
    private static partial void LogFailed(ILogger logger, long songId, Exception exception);

    private sealed record ConvertedFile(string Codec, int? BitrateKbps, string Path);

    private sealed record ConvertHistoryData(ConvertedFile From, ConvertedFile To);
}
