using Compilarr.Core.HealthCheck;

namespace Compilarr.Core.Jobs;

/// <summary>Runs every health check now instead of handing back the cached results.</summary>
public sealed class CheckHealthCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/command</c> uses.</summary>
    public const string CommandName = "CheckHealth";

    private readonly HealthCheckService _healthCheckService;

    /// <summary>Initialises a new instance of the <see cref="CheckHealthCommandHandler"/> class.</summary>
    public CheckHealthCommandHandler(HealthCheckService healthCheckService)
    {
        ArgumentNullException.ThrowIfNull(healthCheckService);

        _healthCheckService = healthCheckService;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var results = await _healthCheckService
            .GetResultsAsync(forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);

        var problems = results.Count(result => result.Type == HealthCheckResult.Error);

        return $"{results.Count} checks, {problems} problems";
    }
}
