using Wondarr.Core.Jobs;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>A service nothing registers, so a handler that needs it cannot be built.</summary>
internal interface IUnregisteredService
{
}

/// <summary>A handler whose constructor needs <see cref="IUnregisteredService"/>, which DI cannot supply.</summary>
internal sealed class UnbuildableCommandHandler : ICommandHandler
{
    public UnbuildableCommandHandler(IUnregisteredService missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
    }

    public string Name => "Unbuildable";

    public Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("never runs");
}

/// <summary>A buildable handler that records that it was disposed.</summary>
internal sealed class DisposableCommandHandler : ICommandHandler, IDisposable
{
    public static int Disposed;

    public string Name => "Disposable";

    public void Dispose() => Interlocked.Increment(ref Disposed);

    public Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("ran");
}
