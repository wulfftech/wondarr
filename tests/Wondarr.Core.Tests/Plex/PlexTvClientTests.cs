using System.Net;
using Wondarr.Core.Plex;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexTvClientTests
{
    private const string ClientIdentifier = "wondarr-test-client";
    private const string Token = "PLEX-USER-TOKEN";

    private static readonly DateTimeOffset PinExpiry = new(2026, 9, 29, 16, 47, 18, TimeSpan.Zero);

    [Fact]
    public async Task CreatePin_posts_the_recorded_request_and_builds_the_auth_url()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("pin-created.json", HttpStatusCode.Created));

        var pin = await CreateClient(handler).CreatePinAsync(ClientIdentifier, CancellationToken.None);

        pin.Id.Should().Be(1234567890);
        pin.Code.Should().Be("abcdefghijklmnopqrstuvwxy");
        pin.ExpiresAt.Should().Be(PinExpiry);
        pin.AuthUrl.Should().Be(
            "https://app.plex.tv/auth#?clientID=wondarr-test-client"
            + "&code=abcdefghijklmnopqrstuvwxy"
            + "&context%5Bdevice%5D%5Bproduct%5D=Wondarr");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be("https://plex.tv/api/v2/pins?strong=true");
    }

    [Fact]
    public async Task CreatePin_escapes_the_client_identifier_in_the_auth_url()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("pin-created.json", HttpStatusCode.Created));

        var pin = await CreateClient(handler).CreatePinAsync("client/with & odd#chars", CancellationToken.None);

        pin.AuthUrl.Should().Contain("clientID=client%2Fwith%20%26%20odd%23chars");
    }

    [Fact]
    public async Task Every_request_carries_the_identifying_headers_and_no_token_in_the_url()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("resources", StringComparison.Ordinal)
                ? PlexFixtures.Json("resources.json")
                : PlexFixtures.Json("pin-created.json", HttpStatusCode.Created));

        var client = CreateClient(handler);
        await client.CreatePinAsync(ClientIdentifier, CancellationToken.None);
        await client.GetServersAsync(Token, ClientIdentifier, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);

        foreach (var request in handler.Requests)
        {
            request.Headers.GetValues("X-Plex-Product").Should().ContainSingle().Which.Should().Be("Wondarr");
            request.Headers.GetValues("X-Plex-Client-Identifier").Should().ContainSingle().Which.Should().Be(ClientIdentifier);
            request.Headers.GetValues("X-Plex-Version").Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
            request.Headers.Accept.Should().ContainSingle().Which.MediaType.Should().Be("application/json");
            request.RequestUri!.AbsoluteUri.Should().NotContain(Token);
            request.RequestUri.Query.Should().NotContain(Token);
        }

        // The token travels in the header, and only on the call that has one.
        handler.Requests[0].Headers.Contains("X-Plex-Token").Should().BeFalse();
        handler.Requests[1].Headers.GetValues("X-Plex-Token").Should().ContainSingle().Which.Should().Be(Token);
    }

    [Fact]
    public async Task CheckPin_reports_a_pending_pin_as_neither_expired_nor_authorised()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("pin-pending.json"));
        var client = CreateClient(handler, new DateTimeOffset(2026, 9, 29, 16, 30, 0, TimeSpan.Zero));

        var status = await client.CheckPinAsync(1234567890, ClientIdentifier, CancellationToken.None);

        status.Expired.Should().BeFalse();
        status.AuthToken.Should().BeNull();
        handler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://plex.tv/api/v2/pins/1234567890");
    }

    [Fact]
    public async Task CheckPin_reports_a_pin_past_its_expiry_as_expired()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("pin-pending.json"));
        var client = CreateClient(handler, new DateTimeOffset(2026, 9, 29, 17, 0, 0, TimeSpan.Zero));

        var status = await client.CheckPinAsync(1234567890, ClientIdentifier, CancellationToken.None);

        status.Expired.Should().BeTrue();
        status.AuthToken.Should().BeNull();
    }

    [Fact]
    public async Task CheckPin_returns_the_token_once_the_user_has_approved()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body(
            """{"id":1234567890,"code":"abcdefghijklmnopqrstuvwxy","expiresAt":"2026-09-29T16:47:18Z","authToken":"PLEX-USER-TOKEN"}"""));

        var client = CreateClient(handler);

        var status = await client.CheckPinAsync(1234567890, ClientIdentifier, CancellationToken.None);

        status.Expired.Should().BeFalse();
        status.AuthToken.Should().Be(Token);
    }

    [Fact]
    public async Task CheckPin_treats_a_pin_plex_tv_has_forgotten_as_expired()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Empty(HttpStatusCode.NotFound));

        var status = await CreateClient(handler).CheckPinAsync(1234567890, ClientIdentifier, CancellationToken.None);

        status.Expired.Should().BeTrue();
        status.AuthToken.Should().BeNull();
    }

    [Fact]
    public async Task GetServers_keeps_only_the_resources_that_provide_a_server()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("resources.json"));

        var servers = await CreateClient(handler).GetServersAsync(Token, ClientIdentifier, CancellationToken.None);

        servers.Should().ContainSingle();
        var server = servers[0];
        server.Name.Should().Be("Test Server 0");
        server.MachineIdentifier.Should().Be("0000000000000000000000000000000000000000");
        server.Owned.Should().BeTrue();
        server.AccessToken.Should().Be(Token);
        server.ProductVersion.Should().Be("1.43.4.10903-e5521bd8c");
        server.Connections.Should().HaveCount(8);
        server.Connections[0].Protocol.Should().Be("https");
        server.Connections[0].Port.Should().Be(32400);
        server.Connections[0].Local.Should().BeTrue();

        handler.Requests[0].RequestUri!.AbsoluteUri.Should()
            .Be("https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1");
    }

    [Fact]
    public async Task A_401_becomes_PlexUnauthorizedException_without_the_token_in_the_message()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body($$"""{"error":"token {{Token}} is invalid"}""", HttpStatusCode.Unauthorized));

        var act = async () => await CreateClient(handler).GetServersAsync(Token, ClientIdentifier, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PlexUnauthorizedException>();
        exception.Which.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task A_500_becomes_PlexException_without_the_token_in_the_message()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body($$"""{"error":"{{Token}}"}""", HttpStatusCode.InternalServerError));

        var act = async () => await CreateClient(handler).GetServersAsync(Token, ClientIdentifier, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PlexException>();
        exception.Which.Message.Should().Contain("500").And.NotContain(Token);
    }

    private static PlexTvClient CreateClient(
        HttpMessageHandler handler,
        DateTimeOffset? now = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://plex.tv/", UriKind.Absolute),
        };

        return new PlexTvClient(
            http,
            Options.Create(new PlexOptions()),
            new FakeTimeProvider(now ?? PinExpiry.AddMinutes(-5)));
    }
}
