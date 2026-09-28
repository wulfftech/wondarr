using Wondarr.Core.Messaging;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Published every time <see cref="HealthCheckService"/> actually ran the checks. A read served
/// from the 60-second cache publishes nothing, so subscribers (the SignalR broadcaster) only learn
/// about fresh results.
/// </summary>
/// <param name="Results">Every check's result, in registration order.</param>
public sealed record HealthCheckCompletedEvent(IReadOnlyList<HealthCheck> Results) : IEvent;
