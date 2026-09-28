using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Compilarr.Api.Tests;

public class PingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Ping_returns_200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}