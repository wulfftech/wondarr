using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.References;

/// <summary>
/// What a person chose for one file of the Match queue: exactly one of a ranked candidate, a
/// MusicBrainz recording id, a MusicBrainz release id, a Deezer track id, or "leave that file alone".
/// </summary>
/// <param name="CandidateRank">The rank of the candidate to accept, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The recording MBID the user named, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id the user named, or <see langword="null"/>.</param>
/// <param name="Skip">Whether the user asked Wondarr to leave the file alone.</param>
/// <param name="Replace">
/// Whether the accepted file should be replaced by a better copy (DECISIONS build session 14 #32): the song
/// is owned through it until one arrives. Not allowed with <paramref name="Skip"/>.
/// </param>
/// <param name="MbReleaseId">
/// The release MBID MusicBrainz sent the user back with after they added the song there, or
/// <see langword="null"/> (DECISIONS build session 14 #33). The file becomes the release's only track, or
/// the one track titled as the file is.
/// </param>
public sealed record ReferenceResolveChoice(
    int? CandidateRank,
    string? MbRecordingId,
    long? DeezerId,
    bool Skip,
    bool Replace = false,
    string? MbReleaseId = null);

/// <summary>What resolving one reference file did.</summary>
/// <param name="State">The state the row is in now.</param>
/// <param name="SongId">The song the file is, or <see langword="null"/> when it was released or skipped.</param>
/// <param name="Message">The line the row carries, for example the duplicate notice, or <see langword="null"/>.</param>
/// <param name="Replacement">What Replace did, for example that a search was queued; <see langword="null"/> without Replace.</param>
public sealed record ReferenceResolveResult(
    ReferenceFileState State,
    long? SongId,
    string? Message,
    string? Replacement = null);

/// <summary>What accepting the top candidates of many files did.</summary>
/// <param name="Resolved">How many files became owned songs.</param>
/// <param name="Failed">How many of the listed files could not be accepted.</param>
/// <param name="Errors">One line per failure, <c>"&lt;relativePath&gt;: &lt;reason&gt;"</c>.</param>
/// <param name="Searching">With Replace, how many of the songs are being searched for a better copy now.</param>
public sealed record ReferenceBulkResult(int Resolved, int Failed, IReadOnlyList<string> Errors, int Searching = 0);

/// <summary>The Match queue and the hand-made choices that clear it (LIBRARY_OUTPUT §7.6).</summary>
public interface IReferenceMatchService
{
    /// <summary>
    /// Lists the files identification could not settle: those with ranked candidates and those where
    /// nothing was found, optionally of one library, paged and sorted.
    /// </summary>
    /// <param name="paging">The page, size, sort key and direction.</param>
    /// <param name="referenceLibraryId">Only files of this library, or <see langword="null"/> for all of them.</param>
    /// <param name="likelyYouTubeRipsOnly">Only the files that are most likely YouTube rips.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of files with their candidates, and the total the filter matched.</returns>
    Task<PagedResult<ReferenceFile>> GetQueueAsync(
        PagingSpec paging,
        long? referenceLibraryId,
        bool likelyYouTubeRipsOnly,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles one file by the user's choice, adding the song and linking the file exactly as automatic
    /// identification does. The user's file is never written, moved or deleted.
    /// </summary>
    /// <param name="referenceFileId">The reference file to settle.</param>
    /// <param name="choice">What the user chose; exactly one of the four.</param>
    /// <param name="cancellationToken">Cancels the lookup and the write.</param>
    /// <returns>The state the row ended in.</returns>
    /// <exception cref="ArgumentException">The choice is not exactly one, or names something this file does not have.</exception>
    /// <exception cref="KeyNotFoundException">No reference file has that id.</exception>
    /// <exception cref="InvalidOperationException">The row is in a state that cannot be resolved, or has no probe.</exception>
    /// <exception cref="SongNotFoundException">Neither provider knows the id the user named.</exception>
    Task<ReferenceResolveResult> ResolveAsync(
        long referenceFileId,
        ReferenceResolveChoice choice,
        CancellationToken cancellationToken);

    /// <summary>
    /// Accepts the best candidate of every listed ambiguous file. The identities are resolved first and
    /// added in one call, so the album policy plans them as a batch: one artist's files do not scatter
    /// into a pseudo-album each.
    /// </summary>
    /// <param name="referenceFileIds">The files to accept, at most <see cref="ReferenceMatchService.MaxBulkSize"/>.</param>
    /// <param name="replace">Whether the accepted files should be replaced by better copies, as Replace does for one.</param>
    /// <param name="cancellationToken">Cancels the lookups and the write.</param>
    /// <returns>How many files were resolved, and why the others were not.</returns>
    /// <exception cref="ArgumentException">More than <see cref="ReferenceMatchService.MaxBulkSize"/> ids.</exception>
    Task<ReferenceBulkResult> AcceptTopCandidatesAsync(
        IReadOnlyList<long> referenceFileIds,
        bool replace,
        CancellationToken cancellationToken);
}

/// <summary>
/// Backs the Match queue. A file is settled by re-using the identification pipeline's own steps — the
/// identity is added by <see cref="ISongService"/> and the file is made the song's file by
/// <see cref="ReferenceOwnership"/> — so a hand-made choice cannot produce a row automatic
/// identification would not have. What differs is only the confidence (the user decided) and the fact
/// that no duration tolerance is applied.
/// </summary>
public sealed partial class ReferenceMatchService : IReferenceMatchService
{
    /// <summary>How the file was settled: by a person, in the Match queue.</summary>
    public const string ManualTier = "manual";

    /// <summary>The confidence a choice a person made carries: it is not a guess.</summary>
    public const double ManualConfidence = 1.0;

    /// <summary>The most ids one bulk accept may carry, so one request cannot lock the queue for long.</summary>
    public const int MaxBulkSize = 500;

    /// <summary>What Replace says when the song is below its cutoff and an Upgrade Search was queued for it.</summary>
    public const string SearchingMessage = "Searching for a better copy now.";

    /// <summary>What Replace says when the file already meets the song's cutoff.</summary>
    public const string MeetsCutoffMessage = "The file already meets its quality profile's cutoff, so nothing replaces it.";

    /// <summary>What Replace says when the song's profile allows no upgrade.</summary>
    public const string NoUpgradesMessage = "Its quality profile does not allow upgrades, so nothing replaces it.";

    /// <summary>What Replace says when the song is not monitored.</summary>
    public const string UnmonitoredMessage = "The song is not monitored, so nothing replaces it.";

    /// <summary>What Replace says when the song holds another file (the duplicate notice), so this one decides nothing.</summary>
    public const string AlreadyHasFileMessage = "The song already holds another file, so Replace changes nothing.";

    /// <summary>What resolving by a release says while MusicBrainz does not show the release the user just added.</summary>
    public const string ReleaseNotShownMessage = "MusicBrainz does not show that release yet. Try again in a minute.";

    /// <summary>What Replace says when the search could not be queued; the scheduled run still finds the song.</summary>
    public const string NotQueuedMessage = "The search could not be queued; the next Upgrade Search looks for a better copy.";

    /// <summary>The tag read and the probe are stored as camelCase JSON, the way the scan wrote them.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WondarrDbContext _database;
    private readonly IIdentityResolver _resolver;
    private readonly IMusicBrainzClient _musicBrainz;
    private readonly ISongService _songs;
    private readonly ICommandQueue _commands;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReferenceMatchService> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReferenceMatchService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="resolver">Resolves the id the user named into a full identity.</param>
    /// <param name="musicBrainz">Reads the release a user added on MusicBrainz, to find the file's recording on it.</param>
    /// <param name="songs">Adds the resolved identity as a song.</param>
    /// <param name="commands">Queues the search Replace starts.</param>
    /// <param name="timeProvider">The clock the import timestamps come from.</param>
    /// <param name="logger">Logs one line per resolve and per bulk run.</param>
    public ReferenceMatchService(
        WondarrDbContext database,
        IIdentityResolver resolver,
        IMusicBrainzClient musicBrainz,
        ISongService songs,
        ICommandQueue commands,
        TimeProvider timeProvider,
        ILogger<ReferenceMatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _resolver = resolver;
        _musicBrainz = musicBrainz;
        _songs = songs;
        _commands = commands;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ReferenceFile>> GetQueueAsync(
        PagingSpec paging,
        long? referenceLibraryId,
        bool likelyYouTubeRipsOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.ReferenceFiles
            .AsNoTracking()
            .Include(row => row.Candidates.OrderBy(candidate => candidate.Rank))
            .Where(row => row.State == ReferenceFileState.Ambiguous || row.State == ReferenceFileState.Unmatched);

        if (referenceLibraryId is { } libraryId)
        {
            query = query.Where(row => row.ReferenceLibraryId == libraryId);
        }

        if (likelyYouTubeRipsOnly)
        {
            query = query.Where(row => row.LikelyYouTubeRip);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ReferenceFile>(records, totalRecords);
    }

    /// <inheritdoc />
    public async Task<ReferenceResolveResult> ResolveAsync(
        long referenceFileId,
        ReferenceResolveChoice choice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);

        RequireOneChoice(choice);

        var row = await _database.ReferenceFiles
            .FirstOrDefaultAsync(candidate => candidate.Id == referenceFileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                string.Concat("No reference file has the id ", referenceFileId.ToString(CultureInfo.InvariantCulture), "."));

        var library = await LibraryAsync(row.ReferenceLibraryId, cancellationToken).ConfigureAwait(false);

        RequireResolvable(row);

        if (choice.Skip)
        {
            return await SkipAsync(row, library, cancellationToken).ConfigureAwait(false);
        }

        RequireProbe(row);

        var (mbRecordingId, deezerId) = choice.CandidateRank is { } rank
            ? await CandidateIdsAsync(row, rank, cancellationToken).ConfigureAwait(false)
            : !string.IsNullOrWhiteSpace(choice.MbReleaseId)
                ? (await RecordingOnReleaseAsync(row, choice.MbReleaseId.Trim(), cancellationToken).ConfigureAwait(false), null)
                : (string.IsNullOrWhiteSpace(choice.MbRecordingId) ? null : choice.MbRecordingId.Trim(), choice.DeezerId);

        var identity = await _resolver
            .GetIdentityAsync(mbRecordingId, deezerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SongNotFoundException(
                string.Concat("No recording with the id ", mbRecordingId ?? deezerId!.Value.ToString(CultureInfo.InvariantCulture), "."));

        // The add leaves its transaction open: the song, its file and the row are committed together.
        Song song;

        try
        {
            try
            {
                song = (await AddAsync(library, [identity], cancellationToken).ConfigureAwait(false))[0];
            }
            catch (DbUpdateException exception) when (SongService.IsDuplicateArtistKey(exception))
            {
                // Another add created one of this song's artists between the artist lookup and the write.
                // Its row is committed now: the add is made once more and reuses it. Whatever the second
                // add throws is final.
                await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);
                DiscardPendingChanges();

                song = (await AddAsync(library, [identity], cancellationToken).ConfigureAwait(false))[0];
            }
            catch (DbUpdateException exception) when (SongService.IsDuplicateSongKey(exception))
            {
                // Another add of this recording saved it between the existence check and the write. The
                // user's choice stands: the file goes to the song that won, as if it had been there all along.
                await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);
                DiscardPendingChanges();

                var winner = await SongService.FindStoredSongAsync(_database, identity, cancellationToken)
                    .ConfigureAwait(false);

                if (winner is null)
                {
                    throw;
                }

                song = winner;
                await EnsureTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            var link = await LinkAsync(row, library, song, cancellationToken).ConfigureAwait(false);

            row.SongId = song.Id;
            row.State = ReferenceFileState.Identified;
            row.Confidence = ManualConfidence;
            row.IdentifiedBy = ManualTier;
            row.Message = link.Message;
            row.Replace = choice.Replace;

            await ClearCandidatesAsync(row, cancellationToken).ConfigureAwait(false);
            await ReferenceOwnership.CommitAsync(_database, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);

            throw;
        }

        LogResolved(_logger, row.Id, song.Id, ManualTier);

        var replacement = choice.Replace
            ? (await StartReplacementAsync([song.Id], cancellationToken).ConfigureAwait(false)).Single
            : null;

        return new ReferenceResolveResult(row.State, row.SongId, row.Message, replacement);
    }

    /// <inheritdoc />
    public async Task<ReferenceBulkResult> AcceptTopCandidatesAsync(
        IReadOnlyList<long> referenceFileIds,
        bool replace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenceFileIds);

        if (referenceFileIds.Count > MaxBulkSize)
        {
            throw new ArgumentException(
                string.Concat("At most ", MaxBulkSize.ToString(CultureInfo.InvariantCulture), " files may be accepted at once."),
                nameof(referenceFileIds));
        }

        var errors = new List<string>();

        if (referenceFileIds.Count == 0)
        {
            return new ReferenceBulkResult(0, 0, errors);
        }

        var rows = await _database.ReferenceFiles
            .Include(row => row.Candidates)
            .Where(row => referenceFileIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, cancellationToken)
            .ConfigureAwait(false);

        var accepted = new List<AcceptedRow>(referenceFileIds.Count);
        var seen = new HashSet<long>();

        foreach (var id in referenceFileIds)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (!rows.TryGetValue(id, out var row))
            {
                errors.Add(string.Concat("#", id.ToString(CultureInfo.InvariantCulture), ": no such reference file"));
                continue;
            }

            if (row.State != ReferenceFileState.Ambiguous)
            {
                errors.Add(string.Concat(row.RelativePath, ": only ambiguous files are accepted in bulk"));
                continue;
            }

            var best = row.Candidates
                .Where(candidate => candidate.Rank == 1)
                .Select(candidate => Read<MatchIdentity>(candidate.Identity))
                .FirstOrDefault(identity => identity is not null);

            if (best is null || (best.MbRecordingId is null && best.DeezerId is null))
            {
                errors.Add(string.Concat(row.RelativePath, ": the file has no candidate to accept"));
                continue;
            }

            if (Read<MediaInfo>(row.Probe) is null)
            {
                errors.Add(string.Concat(row.RelativePath, ": the scan measured nothing about the file"));
                continue;
            }

            var library = await LibraryAsync(row.ReferenceLibraryId, cancellationToken).ConfigureAwait(false);

            // The identities are read first, so that everything the providers still know is added in one
            // batch below; a lookup the provider has forgotten only costs this file its own line.
            var identity = await _resolver
                .GetIdentityAsync(best.MbRecordingId, best.DeezerId, cancellationToken)
                .ConfigureAwait(false);

            if (identity is null)
            {
                errors.Add(string.Concat(row.RelativePath, ": no recording with that id"));
                continue;
            }

            accepted.Add(new AcceptedRow(row, library, identity));
        }

        // One add per library: the album policy plans a batch as a whole, and the usual call is a queue
        // filtered to one library, so this is the one call the pipeline expects.
        var resolved = 0;
        var resolvedSongs = new List<long>();

        foreach (var group in accepted.GroupBy(entry => entry.Library.LibraryId))
        {
            var batch = group.ToList();

            // Each group commits on its own: the add leaves its transaction open, and the songs reach
            // other connections only together with the files and rows that own them.
            var groupResolved = 0;

            try
            {
                IReadOnlyList<Song> added;

                try
                {
                    added = await AddAsync(batch[0].Library, [.. batch.Select(entry => entry.Identity)], cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DbUpdateException racing) when (SongService.IsDuplicateArtistKey(racing))
                {
                    // Another add created one of the batch's artists between the artist lookup and the
                    // write. Its row is committed now: the batch is added once more and reuses it. Whatever
                    // the second add throws is final, and costs this group's files a line each.
                    await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);
                    DiscardPendingChanges();

                    added = await AddAsync(batch[0].Library, [.. batch.Select(entry => entry.Identity)], cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DbUpdateException duplicate) when (SongService.IsDuplicateSongKey(duplicate))
                {
                    // Another add saved one of these recordings between the existence check and the write.
                    // The user's choice stands: a file whose recording now exists goes to that song, and
                    // the others are added as a batch of their own.
                    await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);
                    DiscardPendingChanges();

                    var songs = new Song?[batch.Count];

                    for (var index = 0; index < batch.Count; index++)
                    {
                        songs[index] = await SongService.FindStoredSongAsync(_database, batch[index].Identity, cancellationToken).ConfigureAwait(false);
                    }

                    var missing = Enumerable.Range(0, batch.Count).Where(index => songs[index] is null).ToList();

                    if (missing.Count == batch.Count)
                    {
                        throw;
                    }

                    if (missing.Count > 0)
                    {
                        var rest = await AddAsync(batch[0].Library, [.. missing.Select(index => batch[index].Identity)], cancellationToken)
                            .ConfigureAwait(false);

                        for (var position = 0; position < missing.Count; position++)
                        {
                            songs[missing[position]] = rest[position];
                        }
                    }

                    await EnsureTransactionAsync(cancellationToken).ConfigureAwait(false);
                    added = [.. songs.Select(song => song!)];
                }

                for (var index = 0; index < batch.Count; index++)
                {
                    var entry = batch[index];
                    var link = await LinkAsync(entry.Row, entry.Library, added[index], cancellationToken)
                        .ConfigureAwait(false);

                    entry.Row.SongId = added[index].Id;
                    entry.Row.State = ReferenceFileState.Identified;
                    entry.Row.Confidence = ManualConfidence;
                    entry.Row.IdentifiedBy = ManualTier;
                    entry.Row.Message = link.Message;
                    entry.Row.Replace = replace;

                    await ClearCandidatesAsync(entry.Row, cancellationToken).ConfigureAwait(false);

                    groupResolved++;
                }

                await ReferenceOwnership.CommitAsync(_database, cancellationToken).ConfigureAwait(false);

                resolved += groupResolved;
                resolvedSongs.AddRange(added.Select(song => song.Id));
            }
            catch (Exception exception)
            {
                await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                // One group failing costs its own files a line each; the groups behind it go on.
                DiscardPendingChanges();
                LogGroupFailed(_logger, batch.Count, exception);

                foreach (var entry in batch)
                {
                    errors.Add(string.Concat(entry.Row.RelativePath, ": could not be accepted (", exception.Message, ")"));
                }
            }
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogBulkAccepted(_logger, resolved, errors.Count);

        var searching = replace && resolvedSongs.Count > 0
            ? (await StartReplacementAsync(resolvedSongs, cancellationToken).ConfigureAwait(false)).Searching
            : 0;

        return new ReferenceBulkResult(resolved, errors.Count, errors, searching);
    }

    /// <summary>
    /// Starts looking for better copies of songs whose files were just settled with Replace: one
    /// <c>UpgradeSearch</c> names the songs that are below their cutoff, so they are searched now rather
    /// than at the next scheduled run. A song that meets its cutoff, or whose profile allows no upgrade, is
    /// left as it is and the line says why. A queue that fails does not undo the resolve: the scheduled
    /// Upgrade Search picks the songs up.
    /// </summary>
    /// <param name="songIds">The songs the accepted files were linked to.</param>
    /// <param name="cancellationToken">Cancels the reads and the enqueue.</param>
    /// <returns>How many songs are being searched, and for a single song the line that says what happened.</returns>
    private async Task<(int Searching, string? Single)> StartReplacementAsync(
        IReadOnlyList<long> songIds,
        CancellationToken cancellationToken)
    {
        var replaceable = await ReferenceReplacement
            .ReplaceableSongsAsync(_database, songIds, cancellationToken)
            .ConfigureAwait(false);
        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.File)
            .Where(song => songIds.Contains(song.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var profiles = await _database.QualityProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Id, cancellationToken)
            .ConfigureAwait(false);

        var search = new List<long>();
        string? single = null;

        foreach (var song in songs)
        {
            if (!replaceable.Contains(song.Id) || song.File is null)
            {
                single = AlreadyHasFileMessage;
            }
            else if (!song.Monitored)
            {
                single = UnmonitoredMessage;
            }
            else if (!profiles.TryGetValue(song.QualityProfileId, out var profile) || !profile.UpgradeAllowed)
            {
                single = NoUpgradesMessage;
            }
            else if (profile.MeetsCutoff(song.File.QualityId))
            {
                single = MeetsCutoffMessage;
            }
            else
            {
                search.Add(song.Id);
                single = SearchingMessage;
            }
        }

        if (search.Count == 0)
        {
            return (0, single);
        }

        try
        {
            await _commands
                .EnqueueAsync(
                    UpgradeSearchCommandHandler.CommandName,
                    JsonSerializer.Serialize(new UpgradeSearchRequest(UpgradeSearchCommandHandler.CommandName, search), StoredJson),
                    CommandTrigger.Manual,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReplacementNotQueued(_logger, search.Count, exception);

            return (0, NotQueuedMessage);
        }

        LogReplacementQueued(_logger, search.Count);

        return (search.Count, single);
    }

    /// <summary>
    /// Forgets what a failed group changed, so the next group's save does not write it: added rows are
    /// detached, changed and deleted rows go back to what the database holds.
    /// </summary>
    private void DiscardPendingChanges()
    {
        // Dependents first: letting go of an artist or a song while rows that need it are still tracked
        // would sever a required relationship.
        var entries = _database.ChangeTracker.Entries()
            .OrderBy(entry => entry.Entity switch
            {
                Artist => 2,
                Song => 1,
                _ => 0,
            })
            .ToList();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Releases the song's reference file and marks the row skipped, so the file is left as it is and
    /// the song, if there was one, is wanted again.
    /// </summary>
    private async Task<ReferenceResolveResult> SkipAsync(
        ReferenceFile row,
        ReferenceLibrary library,
        CancellationToken cancellationToken)
    {
        if (row.SongId is { } songId)
        {
            await ReferenceOwnership
                .ReleaseAsync(_database, songId, AbsolutePath(library, row))
                .ConfigureAwait(false);
        }

        row.SongId = null;
        row.State = ReferenceFileState.Skipped;
        row.IdentifiedBy = null;
        row.Message = null;
        row.Replace = false;

        await ClearCandidatesAsync(row, cancellationToken).ConfigureAwait(false);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSkipped(_logger, row.Id);

        return new ReferenceResolveResult(row.State, null, null);
    }

    /// <summary>The ids behind one ranked candidate of a file.</summary>
    private async Task<(string? MbRecordingId, long? DeezerId)> CandidateIdsAsync(
        ReferenceFile row,
        int rank,
        CancellationToken cancellationToken)
    {
        var stored = await _database.MatchCandidates
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ReferenceFileId == row.Id && candidate.Rank == rank, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException(
                string.Concat("Reference file ", row.Id.ToString(CultureInfo.InvariantCulture), " has no candidate with rank ", rank.ToString(CultureInfo.InvariantCulture), "."));

        var identity = Read<MatchIdentity>(stored.Identity)
            ?? throw new ArgumentException(
                string.Concat("Candidate ", rank.ToString(CultureInfo.InvariantCulture), " of reference file ", row.Id.ToString(CultureInfo.InvariantCulture), " is not readable."));

        if (identity.MbRecordingId is null && identity.DeezerId is null)
        {
            throw new ArgumentException(
                string.Concat("Candidate ", rank.ToString(CultureInfo.InvariantCulture), " of reference file ", row.Id.ToString(CultureInfo.InvariantCulture), " names no recording."));
        }

        return (identity.MbRecordingId, identity.DeezerId);
    }

    /// <summary>
    /// The recording a release the user just added on MusicBrainz holds for a file: its only track, or else
    /// the one track whose title is the file's. A release that cannot say which is refused rather than
    /// guessed at: the drawer's search settles the file instead.
    /// </summary>
    private async Task<string> RecordingOnReleaseAsync(
        ReferenceFile row,
        string releaseId,
        CancellationToken cancellationToken)
    {
        var release = await _musicBrainz.GetAddedReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new SongNotFoundException(ReleaseNotShownMessage);

        var tracks = release.Media
            .SelectMany(medium => medium.Tracks)
            .Where(track => !string.IsNullOrEmpty(track.Recording?.Id))
            .ToList();

        if (tracks.Count == 1)
        {
            return tracks[0].Recording!.Id;
        }

        var (_, title) = ReferenceIdentifier.TextOf(Read<FileTags>(row.Tags), row.RelativePath);
        var wanted = title is null ? null : TextMatching.Normalize(title);
        var named = wanted is null
            ? []
            : tracks
                .Where(track => string.Equals(
                    TextMatching.Normalize(track.Title ?? track.Recording!.Title),
                    wanted,
                    StringComparison.Ordinal))
                .ToList();

        if (named.Count == 1)
        {
            return named[0].Recording!.Id;
        }

        throw new InvalidOperationException(
            string.Concat(
                "The release has ",
                tracks.Count.ToString(CultureInfo.InvariantCulture),
                tracks.Count == 1 ? " track" : " tracks",
                named.Count == 0 ? " and none of them is titled \"" : " and more than one is titled \"",
                title ?? row.RelativePath,
                "\". Search for the recording in the drawer instead."));
    }

    /// <summary>
    /// Gives the song the reference file, exactly as automatic identification does: the old song, if the
    /// row was linked to a different one, is freed first. Nothing is ever written to the file on disk.
    /// </summary>
    private async Task<ReferenceOwnership.LinkResult> LinkAsync(
        ReferenceFile row,
        ReferenceLibrary library,
        Song song,
        CancellationToken cancellationToken)
    {
        var path = AbsolutePath(library, row);

        if (row.SongId is { } previous && previous != song.Id)
        {
            await ReferenceOwnership.ReleaseAsync(_database, previous, path).ConfigureAwait(false);
        }

        return await ReferenceOwnership
            .LinkAsync(
                _database,
                song.Id,
                path,
                row,
                Read<MediaInfo>(row.Probe)!,
                row.AcoustId,
                ManualTier,
                _timeProvider.GetUtcNow().UtcDateTime)
            .ConfigureAwait(false);
    }

    /// <summary>Adds one batch of identities the way a reference library's scan does.</summary>
    private async Task<IReadOnlyList<Song>> AddAsync(
        ReferenceLibrary library,
        IReadOnlyList<SongIdentity> identities,
        CancellationToken cancellationToken)
    {
        var results = await _songs
            .AddIdentitiesAsync(identities, ReferenceOwnership.AddOptions(library), cancellationToken)
            .ConfigureAwait(false);

        return [.. results.Select(result => result.Song)];
    }

    /// <summary>
    /// Opens the transaction the write lock is held under when the add did not leave one: a file linked to
    /// a song that won a race commits through the same path as a freshly added song.
    /// </summary>
    private async Task EnsureTransactionAsync(CancellationToken cancellationToken)
    {
        if (_database.Database.CurrentTransaction is null)
        {
            await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A file's absolute path on this machine.</summary>
    private static string AbsolutePath(ReferenceLibrary library, ReferenceFile row) =>
        ReferenceOwnership.AbsolutePath(library.RootPath, row.RelativePath);

    /// <summary>Reads the library a file belongs to.</summary>
    private async Task<ReferenceLibrary> LibraryAsync(long referenceLibraryId, CancellationToken cancellationToken) =>
        await _database.ReferenceLibraries
            .AsNoTracking()
            .FirstAsync(library => library.Id == referenceLibraryId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Deletes the candidates of a row that has been settled: they are no longer a question.</summary>
    private async Task ClearCandidatesAsync(ReferenceFile row, CancellationToken cancellationToken)
    {
        var stale = await _database.MatchCandidates
            .Where(candidate => candidate.ReferenceFileId == row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _database.MatchCandidates.RemoveRange(stale);
    }

    /// <summary>Exactly one of the five choices has to be given; "all of them" and "none" are both wrong.</summary>
    private static void RequireOneChoice(ReferenceResolveChoice choice)
    {
        var chosen = (choice.Skip ? 1 : 0)
            + (choice.CandidateRank is not null ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(choice.MbRecordingId) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(choice.MbReleaseId) ? 1 : 0)
            + (choice.DeezerId is not null ? 1 : 0);

        if (chosen != 1)
        {
            throw new ArgumentException(
                "Exactly one of skip, candidateRank, mbRecordingId, mbReleaseId or deezerId must be given.",
                nameof(choice));
        }

        if (choice.Skip && choice.Replace)
        {
            throw new ArgumentException("replace cannot be combined with skip: a skipped file owns no song.", nameof(choice));
        }
    }

    /// <summary>
    /// A file may be re-resolved when the user disagrees with the last answer: an accepted file, a
    /// skipped one and an unsettled one all can be. What is still being worked out, gone or already
    /// moved cannot.
    /// </summary>
    private static void RequireResolvable(ReferenceFile row)
    {
        if (row.State is ReferenceFileState.Ambiguous
            or ReferenceFileState.Unmatched
            or ReferenceFileState.Identified
            or ReferenceFileState.Skipped)
        {
            return;
        }

        throw new InvalidOperationException(
            string.Concat(
                "Reference file ",
                row.Id.ToString(CultureInfo.InvariantCulture),
                " cannot be resolved while it is ",
                row.State.ToString().ToLowerInvariant(),
                "."));
    }

    /// <summary>Without a probe there is nothing to write onto the song's file row.</summary>
    private static void RequireProbe(ReferenceFile row)
    {
        if (Read<MediaInfo>(row.Probe) is null)
        {
            throw new InvalidOperationException(
                string.Concat(
                    "Reference file ",
                    row.Id.ToString(CultureInfo.InvariantCulture),
                    " cannot be linked: the scan measured nothing about it."));
        }
    }

    /// <summary>
    /// Sort keys are case-insensitive; anything unknown — including the default — is the relative path.
    /// The id breaks ties, so paging the same query twice cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<ReferenceFile> ApplySort(IQueryable<ReferenceFile> query, PagingSpec paging) =>
        paging.SortKey?.ToLowerInvariant() switch
        {
            "state" => By(query, row => row.State, paging.Descending),
            _ => By(query, row => row.RelativePath, paging.Descending),
        };

    private static IQueryable<ReferenceFile> By<TKey>(
        IQueryable<ReferenceFile> query,
        Expression<Func<ReferenceFile, TKey>> key,
        bool descending)
    {
        var ordered = descending ? query.OrderByDescending(key) : query.OrderBy(key);

        return ordered.ThenBy(row => row.Id);
    }

    /// <summary>Reads one of the scan's stored JSON blobs, or <see langword="null"/> when there is none.</summary>
    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, StoredJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One file a bulk accept is about to settle.</summary>
    private sealed record AcceptedRow(ReferenceFile Row, ReferenceLibrary Library, SongIdentity Identity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolved reference file {ReferenceFileId} as song {SongId} ({IdentifiedBy})")]
    private static partial void LogResolved(ILogger logger, long referenceFileId, long songId, string identifiedBy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reference file {ReferenceFileId} was skipped; the file is left alone")]
    private static partial void LogSkipped(ILogger logger, long referenceFileId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting the best candidate of {Files} reference files failed; the rest of the batch goes on")]
    private static partial void LogGroupFailed(ILogger logger, int files, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted the best candidate of {Resolved} reference files; {Failed} could not be accepted")]
    private static partial void LogBulkAccepted(ILogger logger, int resolved, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replace: queued an Upgrade Search for {Songs} songs")]
    private static partial void LogReplacementQueued(ILogger logger, int songs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replace: the Upgrade Search for {Songs} songs could not be queued; the scheduled run will find them")]
    private static partial void LogReplacementNotQueued(ILogger logger, int songs, Exception exception);

    /// <summary>The body of the Upgrade Search Replace queues: the songs to look at.</summary>
    /// <param name="Name">The command name, as the API's own bodies carry it.</param>
    /// <param name="SongIds">The songs to search.</param>
    private sealed record UpgradeSearchRequest(string Name, List<long> SongIds);
}
