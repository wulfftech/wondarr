using System.Net;
using Wondarr.Core.Plex;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexServerClientTests
{
    private const string Token = "PLEX-USER-TOKEN";

    private static readonly Uri Server = new("https://plex.example:32400/");

    [Fact]
    public async Task GetIdentity_reads_the_recorded_identity()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("identity.json"));

        var identity = await CreateClient(handler).GetIdentityAsync(Server, Token, CancellationToken.None);

        identity.MachineIdentifier.Should().Be("0000000000000000000000000000000000000000");
        identity.Version.Should().Be("1.43.4.10903-e5521bd8c");
        identity.Claimed.Should().BeTrue();
        handler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://plex.example:32400/identity");
    }

    [Fact]
    public async Task GetSections_reads_the_recorded_library_list()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("sections.json"));

        var sections = await CreateClient(handler).GetSectionsAsync(Server, Token, CancellationToken.None);

        sections.Should().HaveCount(6);
        sections.Should().Contain(section => section.Key == "5" && section.Type == "artist" && section.Title == "Artist 3");
        sections.Should().Contain(section => section.Key == "1" && section.Type == "movie");

        var music = sections.Single(section => section.Key == "5");
        music.Refreshing.Should().BeFalse();
        music.Locations.Should().Equal("/data/artist3");
    }

    [Fact]
    public async Task GetSections_accepts_a_section_key_sent_as_a_number()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body(
            """{"MediaContainer":{"size":1,"Directory":[{"key":7,"type":"artist","title":"Music","refreshing":true,"Location":[{"id":1,"path":"/Music"}]}]}}"""));

        var sections = await CreateClient(handler).GetSectionsAsync(Server, Token, CancellationToken.None);

        sections.Should().ContainSingle();
        sections[0].Key.Should().Be("7");
        sections[0].Refreshing.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshPath_escapes_the_path_and_asks_over_GET()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Empty());

        await CreateClient(handler).RefreshPathAsync(
            Server,
            Token,
            "5",
            "/Music/A & B#C/Ünicode",
            CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://plex.example:32400/library/sections/5/refresh"
            + "?path=%2FMusic%2FA%20%26%20B%23C%2F%C3%9Cnicode");
    }

    [Fact]
    public async Task EmptyTrash_is_a_PUT()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Empty());

        await CreateClient(handler).EmptyTrashAsync(Server, Token, "5", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.RequestUri!.AbsoluteUri.Should().Be("https://plex.example:32400/library/sections/5/emptyTrash");
    }

    [Fact]
    public async Task IsRefreshing_reads_the_section_flag_and_is_false_for_a_section_that_is_gone()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body(
            """{"MediaContainer":{"Directory":[{"key":"5","type":"artist","title":"Music","refreshing":true}]}}"""));

        var client = CreateClient(handler);

        (await client.IsRefreshingAsync(Server, Token, "5", CancellationToken.None)).Should().BeTrue();
        (await client.IsRefreshingAsync(Server, Token, "99", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_server_url_with_a_path_prefix_keeps_it()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("identity.json"));
        var server = new Uri("https://plex.example/plex", UriKind.Absolute);

        await CreateClient(handler).GetIdentityAsync(server, Token, CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://plex.example/plex/identity");
    }

    [Fact]
    public async Task Every_request_carries_the_identifying_headers_and_the_token_only_as_a_header()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Json("identity.json"));

        await CreateClient(handler).GetIdentityAsync(Server, Token, CancellationToken.None);

        var request = handler.Requests[0];
        request.Headers.GetValues("X-Plex-Product").Should().ContainSingle().Which.Should().Be("Wondarr");
        request.Headers.GetValues("X-Plex-Client-Identifier").Should().ContainSingle().Which.Should().Be("client-identifier");
        request.Headers.GetValues("X-Plex-Version").Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        request.Headers.GetValues("X-Plex-Token").Should().ContainSingle().Which.Should().Be(Token);
        request.RequestUri!.AbsoluteUri.Should().NotContain(Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_credential_becomes_PlexUnauthorizedException(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body($$"""{"error":"{{Token}}"}""", status));

        var act = async () => await CreateClient(handler).GetIdentityAsync(Server, Token, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PlexUnauthorizedException>();
        exception.Which.Message.Should().Contain(((int)status).ToString()).And.NotContain(Token);
    }

    [Fact]
    public async Task A_server_error_becomes_PlexException_without_the_token_or_the_query()
    {
        var handler = new StubHttpMessageHandler(_ => PlexFixtures.Body($$"""{"error":"{{Token}}"}""", HttpStatusCode.InternalServerError));

        var act = async () => await CreateClient(handler).RefreshPathAsync(
            Server,
            Token,
            "5",
            "/Music/Artist & Band",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PlexException>();
        exception.Which.Message.Should().Contain("500");
        exception.Which.Message.Should().NotContain(Token);
        exception.Which.Message.Should().NotContain("Artist & Band");
    }

    [Fact]
    public async Task A_timeout_becomes_PlexException_without_the_token()
    {
        var handler = new SlowHttpMessageHandler();
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(50));

        var act = async () => await client.GetIdentityAsync(Server, Token, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PlexException>();
        exception.Which.Message.Should().NotContain(Token);
    }

    private static PlexServerClient CreateClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var identifier = Substitute.For<IPlexClientIdentifier>();
        identifier.GetAsync(Arg.Any<CancellationToken>()).Returns("client-identifier");

        return new PlexServerClient(new StubHttpClientFactory(handler, timeout), identifier);
    }
}
