using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Sources;

/// <summary>Reads and writes search runs and the candidates they saw.</summary>
public interface ISearchRunService
{
    /// <summary>Opens a run for a song.</summary>
    /// <param name="songId">The song being searched for.</param>
    /// <param name="trigger">What started the search.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored run, with its id set and <c>FinishedAt</c> still null.</returns>
    Task<SearchRun> StartAsync(long songId, SearchTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Stores the candidates the run saw, in one write.</summary>
    /// <param name="searchRunId">The run that saw them.</param>
    /// <param name="candidates">The candidates; their <c>SearchRunId</c> is set here.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task AddCandidatesAsync(long searchRunId, IReadOnlyList<CandidateRecord> candidates, CancellationToken cancellationToken);

    /// <summary>Closes a run: when it ended, how, what it asked and how many candidates it stored.</summary>
    /// <param name="searchRunId">The run to close.</param>
    /// <param name="outcome">How the run ended.</param>
    /// <param name="sources">The source types that were asked.</param>
    /// <param name="queries">The search texts that were sent.</param>
    /// <param name="message">A note for the history screen, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task FinishAsync(
        long searchRunId,
        SearchOutcome outcome,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> queries,
        string? message,
        CancellationToken cancellationToken);

    /// <summary>Gets the candidates a run saw, best score first.</summary>
    /// <param name="searchRunId">The run.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<CandidateRecord>> GetCandidatesAsync(long searchRunId, CancellationToken cancellationToken);

    /// <summary>Gets the song's most recent run, or <see langword="null"/> when it was never searched.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<SearchRun?> GetLatestAsync(long songId, CancellationToken cancellationToken);

    /// <summary>Gets when the song was last searched, newest first. The backoff is computed from these.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="take">How many starts to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<DateTime>> GetRecentStartsAsync(long songId, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Records the search-and-grab loop's evidence: every run, every candidate it saw with its score and
/// rejections, so the interactive search and the history can show why (ARCHITECTURE §5.4).
/// </summary>
public sealed class SearchRunService : ISearchRunService
{
    private readonly WondarrDbContext _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="SearchRunService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="timeProvider">The clock used to date the runs.</param>
    public SearchRunService(WondarrDbContext database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<SearchRun> StartAsync(long songId, SearchTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new SearchRun
        {
            SongId = songId,
            Trigger = trigger,
            StartedAt = _timeProvider.GetUtcNow().UtcDateTime,
        };

        _database.SearchRuns.Add(run);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <inheritdoc />
    public async Task AddCandidatesAsync(
        long searchRunId,
        IReadOnlyList<CandidateRecord> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            candidate.SearchRunId = searchRunId;
            _database.Candidates.Add(candidate);
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinishAsync(
        long searchRunId,
        SearchOutcome outcome,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> queries,
        string? message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(queries);

        var run = await _database.SearchRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == searchRunId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return;
        }

        run.FinishedAt = _timeProvider.GetUtcNow().UtcDateTime;
        run.Outcome = outcome;
        run.Sources = [.. sources];
        run.Queries = [.. queries];
        run.Message = message;
        run.CandidateCount = await _database.Candidates
            .CountAsync(candidate => candidate.SearchRunId == searchRunId, cancellationToken)
            .ConfigureAwait(false);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateRecord>> GetCandidatesAsync(
        long searchRunId,
        CancellationToken cancellationToken) =>
        await _database.Candidates
            .AsNoTracking()
            .Where(candidate => candidate.SearchRunId == searchRunId)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SearchRun?> GetLatestAsync(long songId, CancellationToken cancellationToken) =>
        await _database.SearchRuns
            .AsNoTracking()
            .Where(run => run.SongId == songId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTime>> GetRecentStartsAsync(
        long songId,
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        return await _database.SearchRuns
            .AsNoTracking()
            .Where(run => run.SongId == songId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Take(take)
            .Select(run => run.StartedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}