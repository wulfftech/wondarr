using Wondarr.Core.HealthCheck;

// The test namespace ends in "HealthCheck", which shadows the result type of the same name, so it
// needs an alias here.
using HealthCheckModel = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Tests.HealthCheck;

/// <summary>A check that counts how often it ran and returns whatever it was told to.</summary>
internal sealed class StubHealthCheck : IHealthCheck
{
    private readonly Func<HealthCheckModel> _result;

    /// <param name="name">The name reported as the result's source.</param>
    /// <param name="result">Builds the result; defaults to an <c>Ok</c> result naming the check.</param>
    public StubHealthCheck(string name, Func<HealthCheckModel>? result = null)
    {
        Name = name;
        _result = result ?? (() => new HealthCheckModel(name, HealthCheckResult.Ok, $"{name} is fine", null));
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>How many times <see cref="CheckAsync"/> has been called.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<HealthCheckModel> CheckAsync(CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(_result());
    }
}
