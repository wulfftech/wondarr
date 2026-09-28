using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>What one Soulseek search produced.</summary>
/// <param name="SearchText">The text that was searched for.</param>
/// <param name="Responses">The peer responses, read once and only after the search ended.</param>
/// <param name="FinalState">slskd's own state string, or <c>Missing</c> if the search vanished.</param>
/// <param name="StoppedByWallClock">Whether Wondarr stopped the search instead of slskd finishing it.</param>
/// <param name="Elapsed">How long the run took, measured on <see cref="TimeProvider"/>.</param>
public sealed record SlskdSearchResult(
    string SearchText,
    IReadOnlyList<SlskdSearchResponse> Responses,
    string FinalState,
    bool StoppedByWallClock,
    TimeSpan Elapsed);

/// <summary>
/// Runs one Soulseek search from end to end. This is the only way Wondarr talks to slskd's search
/// API: the budget is taken first, the search is polled to completion (or stopped by our own wall
/// clock), its responses are read once, and it is deleted from slskd on every path out.
/// </summary>
public interface ISlskdSearchRunner
{
    /// <summary>Runs one search and returns everything the network answered with.</summary>
    /// <param name="searchText">The query to send.</param>
    /// <param name="cancellationToken">Cancels the run; the search is still deleted.</param>
    Task<SlskdSearchResult> RunAsync(string searchText, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class SlskdSearchRunner : ISlskdSearchRunner
{
    /// <summary>How long the delete gets before it is abandoned.</summary>
    public static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopped search gets to report itself complete before we read it anyway.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    /// <summary>State reported when slskd no longer knows about the search.</summary>
    public const string MissingState = "Missing";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISoulseekSearchBudget _budget;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlskdSearchRunner> _logger;

    /// <summary>Initialises a new instance of the <see cref="SlskdSearchRunner"/> class.</summary>
    /// <param name="scopeFactory">
    /// The runner is a singleton and the search API is a typed client built from scoped secrets, so
    /// each run resolves its own client from a scope of its own.
    /// </param>
    /// <param name="budget">The process-wide search budget every submission passes.</param>
    /// <param name="options">Soulseek settings, whose <c>search</c> section holds the parameters.</param>
    /// <param name="timeProvider">The clock the wall clock and the poll interval are measured on.</param>
    /// <param name="logger">One Information line per search.</param>
    public SlskdSearchRunner(
        IServiceScopeFactory scopeFactory,
        ISoulseekSearchBudget budget,
        IOptionsMonitor<SoulseekOptions> options,
        TimeProvider timeProvider,
        ILogger<SlskdSearchRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _budget = budget;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SlskdSearchResult> RunAsync(string searchText, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        var search = _options.CurrentValue.Search;
        var startedAt = _timeProvider.GetTimestamp();

        await using var lease = await _budget.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _scopeFactory.CreateScope();

        var api = scope.ServiceProvider.GetRequiredService<ISlskdSearchApi>();

        var id = Guid.NewGuid();
        var created = false;
        SlskdSearch? state = null;
        IReadOnlyList<SlskdSearchResponse> responses = [];
        var stoppedByWallClock = false;

        try
        {
            state = await api
                .StartAsync(Request(id, searchText, search), cancellationToken)
                .ConfigureAwait(false);
            created = true;

            var pollInterval = TimeSpan.FromMilliseconds(search.PollIntervalMs);
            var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(search.WallClockSeconds);

            while (!state.IsComplete)
            {
                var remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    stoppedByWallClock = true;
                    break;
                }

                await Task.Delay(remaining < pollInterval ? remaining : pollInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);

                state = await api.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    break;
                }
            }

            if (stoppedByWallClock)
            {
                state = await StopAndSettleAsync(api, id, pollInterval, cancellationToken).ConfigureAwait(false);
            }

            // Responses are only readable once the search has ended, so they are read exactly once.
            if (state is not null)
            {
                responses = await api.GetResponsesAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever happened above — completion, wall clock, cancellation, a failed read — the
            // search must not be left behind on slskd. Nothing was created if StartAsync threw.
            if (created)
            {
                await DeleteAsync(api, id).ConfigureAwait(false);
            }
        }

        var elapsed = _timeProvider.GetElapsedTime(startedAt);
        var finalState = state?.State ?? MissingState;
        var fileCount = responses.Sum(response => response.FileCount);

        LogSearchFinished(
            _logger,
            searchText,
            finalState,
            responses.Count,
            fileCount,
            (long)elapsed.TotalMilliseconds,
            stoppedByWallClock);

        return new SlskdSearchResult(searchText, responses, finalState, stoppedByWallClock, elapsed);
    }

    private static SlskdSearchRequest Request(Guid id, string searchText, SoulseekSearchOptions search) =>
        new(
            id,
            searchText,
            search.SearchTimeoutMs,
            search.ResponseLimit,
            search.FileLimit,
            search.MinimumPeerUploadSpeed);

    /// <summary>Stops a search the wall clock ran out on, then gives slskd a moment to settle.</summary>
    private async Task<SlskdSearch?> StopAndSettleAsync(
        ISlskdSearchApi api,
        Guid id,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        await api.StopAsync(id, cancellationToken).ConfigureAwait(false);

        var settleBy = _timeProvider.GetUtcNow() + StopGrace;
        var state = await api.GetAsync(id, cancellationToken).ConfigureAwait(false);

        while (state is { IsComplete: false } && _timeProvider.GetUtcNow() < settleBy)
        {
            await Task.Delay(pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            state = await api.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return state;
    }

    /// <summary>
    /// Deletes the search with a token of its own: the caller's token may already be cancelled, and
    /// a failed delete must not fail the run that is unwinding past it.
    /// </summary>
    private async Task DeleteAsync(ISlskdSearchApi api, Guid id)
    {
        using var timeout = new CancellationTokenSource(DeleteTimeout, _timeProvider);

        try
        {
            await api.DeleteAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A search that cannot be deleted is a leak, not a failed run: log it and carry on.
            LogDeleteFailed(_logger, id, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Soulseek search {SearchText} ended in {FinalState} with {ResponseCount} responses and {FileCount} files after {ElapsedMs} ms (stopped by wall clock: {StoppedByWallClock})")]
    private static partial void LogSearchFinished(
        ILogger logger,
        string searchText,
        string finalState,
        int responseCount,
        int fileCount,
        long elapsedMs,
        bool stoppedByWallClock);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the Soulseek search {SearchId} from slskd; it may still be running")]
    private static partial void LogDeleteFailed(ILogger logger, Guid searchId, Exception exception);
}