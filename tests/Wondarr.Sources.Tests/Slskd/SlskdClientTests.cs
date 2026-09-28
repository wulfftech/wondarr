using System.Net;
using Wondarr.Core.Logging;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdClientTests
{
    [Fact]
    public async Task Sends_the_api_key_header_and_parses_a_logged_in_state()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("application-logged-in.json"));

        var state = await Client(handler).GetApplicationStateAsync(CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues(SlskdClient.ApiKeyHeader)
            .Should().ContainSingle().Which.Should().Be(SlskdTestData.Secrets.ApiKey);
        handler.Requests[0].RequestUri!.ToString().Should().Be("http://127.0.0.1:5030/api/v0/application");

        state.Version.Current.Should().Be("0.26.0.0");
        state.Server.IsLoggedIn.Should().BeTrue();
        state.Server.IsConnected.Should().BeTrue();
        state.PendingRestart.Should().BeFalse();
        state.User.Username.Should().Be("wondarr-test");
    }

    [Fact]
    public async Task Parses_a_logged_out_state()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("application-logged-out.json"));

        var state = await Client(handler).GetApplicationStateAsync(CancellationToken.None);

        state.Server.IsLoggedIn.Should().BeFalse();
        state.Server.State.Should().Be("Disconnected");
        state.PendingRestart.Should().BeTrue();
        state.PendingReconnect.Should().BeTrue();
    }

    [Fact]
    public async Task Surfaces_a_401_as_an_http_request_exception()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized);

        var act = () => Client(handler).GetApplicationStateAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static SlskdClient Client(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var secrets = new SlskdSecretsStore(
            SlskdTestData.RepositoryWithRuntimeSecrets(),
            Substitute.For<ISecretRegistry>(),
            SlskdTestData.Monitor(new SoulseekOptions()));

        return new SlskdClient(http, SlskdTestData.Monitor(new SoulseekOptions()), secrets);
    }
}
