using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWondarrCore_returns_the_same_service_collection()
    {
        var services = Substitute.For<IServiceCollection>();

        services.AddWondarrCore().Should().BeSameAs(services);
    }
}
