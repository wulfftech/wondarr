using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWondarrCore_returns_the_same_service_collection()
    {
        // A real collection, not a substitute: AddWondarrCore registers named HTTP clients, which is
        // something a substitute's empty service list cannot carry.
        var services = new ServiceCollection();

        services.AddWondarrCore().Should().BeSameAs(services);
    }
}
