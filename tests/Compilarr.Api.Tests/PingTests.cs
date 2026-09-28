using System.Net;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

public sealed class PingTests : IDisposable
{
    private readonly CompilarrAppFactory _factory = new();

    [Fact]
    public async Task Ping_returns_200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose() => _factory.Dispose();
}
