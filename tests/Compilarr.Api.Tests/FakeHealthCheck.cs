using Compilarr.Core.HealthCheck;

namespace Compilarr.Api.Tests;

/// <summary>A health check the tests register to force a particular result.</summary>
internal sealed class FakeHealthCheck : IHealthCheck
{
    private readonly Func<HealthCheck> _result;

    public FakeHealthCheck(string name, Func<HealthCheck> result)
    {
        Name = name;
        _result = result;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<HealthCheck> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(_result());
}
