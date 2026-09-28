// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/HealthCheck/HealthCheckService.cs, GPL-3.0.
// Adapted for Compilarr: no event aggregation, debouncing or scheduling — run every check, cache
// the results for 60 seconds and hand them back.

using Compilarr.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.HealthCheck;

/// <summary>
/// Runs every registered <see cref="IHealthCheck"/> and caches the results. A singleton, so the cache is
/// shared across requests; each run resolves the checks in a fresh DI scope because some (the
/// database check) depend on scoped services.
/// </summary>
public sealed partial class HealthCheckService : IDisposable
{
    /// <summary>How long a set of results is reused before the checks run again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<HealthCheck>? _results;
    private DateTimeOffset _resultsAt;

    /// <summary>Initialises a new instance of the <see cref="HealthCheckService"/> class.</summary>
    public HealthCheckService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IEventAggregator eventAggregator,
        ILogger<HealthCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    /// <summary>
    /// Returns the results of every registered check, reusing the previous run when it is younger
    /// than <see cref="CacheLifetime"/>.
    /// </summary>
    /// <param name="forceRefresh">Runs the checks even when a cached result is still fresh.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    public async Task<IReadOnlyList<HealthCheck>> GetResultsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cached = GetCachedResults();
        if (cached is not null && !forceRefresh)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<HealthCheck> results;

        try
        {
            // Another caller may have refreshed the cache while this one waited for the gate.
            cached = GetCachedResults();
            if (cached is not null && !forceRefresh)
            {
                return cached;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var checks = scope.ServiceProvider.GetServices<IHealthCheck>().ToList();
            var fresh = new List<HealthCheck>(checks.Count);

            foreach (var check in checks)
            {
                fresh.Add(await RunAsync(check, cancellationToken).ConfigureAwait(false));
            }

            _results = fresh;
            _resultsAt = _timeProvider.GetUtcNow();
            results = fresh;
        }
        finally
        {
            _gate.Release();
        }

        // Published outside the gate: a handler that asked for the results back would deadlock
        // inside it. Only a real run gets here — a cached read returned above.
        await _eventAggregator
            .PublishAsync(new HealthCheckCompletedEvent(results), cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private IReadOnlyList<HealthCheck>? GetCachedResults() =>
        _results is not null && _timeProvider.GetUtcNow() - _resultsAt < CacheLifetime ? _results : null;

    private async Task<HealthCheck> RunAsync(IHealthCheck check, CancellationToken cancellationToken)
    {
        try
        {
            return await check.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCheckFailed(check.Name, exception.Message);

            return new HealthCheck(
                check.Name,
                HealthCheckResult.Error,
                $"{check.Name} failed: {exception.Message}",
                null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check {CheckName} failed: {Reason}")]
    private partial void LogCheckFailed(string checkName, string reason);
}
