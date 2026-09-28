using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Compilarr.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCompilarrCore_returns_the_same_service_collection()
    {
        var services = Substitute.For<IServiceCollection>();

        services.AddCompilarrCore().Should().BeSameAs(services);
    }
}
