using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Organizer;

/// <summary>
/// At startup, once the database is migrated, marks the root of every library that qualifies
/// (<see cref="ILibraryRootAdopter"/>), so libraries that predate the marker keep importing. The
/// library root health check repeats the pass, so a share that was down at this moment is adopted
/// when it returns.
/// </summary>
public sealed partial class LibraryRootAdoptionHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LibraryRootAdoptionHostedService> _logger;

    /// <summary>Initialises a new instance of the <see cref="LibraryRootAdoptionHostedService"/> class.</summary>
    /// <param name="scopeFactory">Gives the adopter a scope of its own.</param>
    /// <param name="logger">The log sink.</param>
    public LibraryRootAdoptionHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<LibraryRootAdoptionHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var adopter = scope.ServiceProvider.GetRequiredService<ILibraryRootAdopter>();
            await adopter.AdoptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A start-up nicety must never stop the host: the health check repeats the pass.
            LogFailed(_logger, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not mark the library roots at startup")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
