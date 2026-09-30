using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Api.Plex;
using Wondarr.Core.Plex;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Plex settings endpoints: the PIN sign-in, the account's servers, the selected server and its
/// music sections. plex.tv and the Plex Media Server are NSubstitute fakes, so no test reaches the
/// network, and every response body is kept so the tests can prove no token ever leaves the server.
/// </summary>
public sealed class PlexApiTests
{
    private const string Endpoint = "/api/v1/plex";

    /// <summary>The account token the fakes hand out. Must never appear in a response.</summary>
    private const string Token = "plex-token-2f8c1d47e6a94b03";

    /// <summary>The per-server token plex.tv hands out. Must never appear in a response either.</summary>
    private const string ServerToken = "server-token-8b1e5a92c7f34d60";

    private const string ServerUrl = "https://192-168-1-10.abc.plex.direct:32400";
    private const string MachineIdentifier = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string ServerVersion = "1.41.3.9314";
    private const long PinId = 4242;

    [Fact]
    public async Task The_pin_flow_signs_in_without_ever_returning_the_token()
    {
        var tv = Substitute.For<IPlexTvClient>();
        tv.CreatePinAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPin(
                PinId,
                "ABCD1234",
                DateTimeOffset.UtcNow.AddMinutes(30),
                "https://app.plex.tv/auth#?clientID=abc&code=ABCD1234")));

        // Pending until the user approves the code on plex.tv, then plex.tv hands over the token.
        tv.CheckPinAsync(PinId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: null)),
                Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: Token)));

        using var factory = Factory(tv, Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);

        var (created, pinBody) = await api.PostAsync($"{Endpoint}/pin");
        created.Should().Be(HttpStatusCode.Created);

        var pin = (JsonObject)pinBody!;
        pin["id"]!.GetValue<long>().Should().Be(PinId);
        pin["code"]!.GetValue<string>().Should().Be("ABCD1234");
        pin["authUrl"]!.GetValue<string>().Should().Contain("ABCD1234");
        pin["expiresAt"].Should().NotBeNull();

        var (pending, pendingBody) = await api.GetAsync($"{Endpoint}/pin/{PinId}");
        pending.Should().Be(HttpStatusCode.OK);
        ((JsonObject)pendingBody!)["authorized"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)pendingBody!)["expired"]!.GetValue<bool>().Should().BeFalse();

        var (approved, approvedBody) = await api.GetAsync($"{Endpoint}/pin/{PinId}");
        approved.Should().Be(HttpStatusCode.OK);
        ((JsonObject)approvedBody!)["authorized"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)approvedBody!)["expired"]!.GetValue<bool>().Should().BeFalse();

        var (state, stateBody) = await api.GetAsync(Endpoint);
        state.Should().Be(HttpStatusCode.OK);
        ((JsonObject)stateBody!)["signedIn"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)stateBody!)["clientIdentifier"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        api.AssertNoSecret(Token);
    }

    [Fact]
    public async Task An_expired_pin_is_reported_as_expired_and_not_authorized()
    {
        var tv = Substitute.For<IPlexTvClient>();
        tv.CheckPinAsync(PinId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPinStatus(Expired: true, AuthToken: null)));

        using var factory = Factory(tv, Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);

        var (status, body) = await api.GetAsync($"{Endpoint}/pin/{PinId}");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["expired"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)body!)["authorized"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_pending_pin_is_not_authorized_on_an_install_that_is_already_signed_in()
    {
        var tv = Tv();
        tv.CheckPinAsync(PinId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: null)));

        using var factory = Factory(tv, Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);
        await SignInAsync(api);

        var (status, body) = await api.GetAsync($"{Endpoint}/pin/{PinId}");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["authorized"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Listing_servers_maps_the_accounts_servers_without_their_tokens()
    {
        using var factory = Factory(Tv(), Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);
        await SignInAsync(api);

        var (status, body) = await api.GetAsync($"{Endpoint}/servers");

        status.Should().Be(HttpStatusCode.OK);

        var servers = (JsonArray)body!;
        servers.Should().ContainSingle();

        var server = (JsonObject)servers[0]!;
        server["name"]!.GetValue<string>().Should().Be("Living Room");
        server["machineIdentifier"]!.GetValue<string>().Should().Be(MachineIdentifier);
        server["owned"]!.GetValue<bool>().Should().BeTrue();
        server["productVersion"]!.GetValue<string>().Should().Be(ServerVersion);

        var connections = (JsonArray)server["connections"]!;
        connections.Should().ContainSingle();

        var connection = (JsonObject)connections[0]!;
        connection["uri"]!.GetValue<string>().Should().Be(ServerUrl);
        connection["local"]!.GetValue<bool>().Should().BeTrue();
        connection["relay"]!.GetValue<bool>().Should().BeFalse();

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Selecting_a_server_stores_it_and_returns_the_state()
    {
        using var factory = Factory(Tv(), ServerClient());
        using var api = new Session(factory);
        await SignInAsync(api);

        var (selected, body) = await api.PutAsync($"{Endpoint}/server", new { serverUrl = ServerUrl });

        selected.Should().Be(HttpStatusCode.OK);

        var state = (JsonObject)body!;
        state["signedIn"]!.GetValue<bool>().Should().BeTrue();
        state["serverUrl"]!.GetValue<string>().Should().Be(ServerUrl);
        state["serverName"]!.GetValue<string>().Should().Be("Living Room");
        state["machineIdentifier"]!.GetValue<string>().Should().Be(MachineIdentifier);

        var (reread, rereadBody) = await api.GetAsync(Endpoint);
        reread.Should().Be(HttpStatusCode.OK);
        ((JsonObject)rereadBody!)["serverUrl"]!.GetValue<string>().Should().Be(ServerUrl);

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Sections_lists_only_the_music_sections()
    {
        using var factory = Factory(Tv(), ServerClient());
        using var api = new Session(factory);
        await SignInAsync(api);
        await SelectServerAsync(api);

        var (status, body) = await api.GetAsync($"{Endpoint}/sections");

        status.Should().Be(HttpStatusCode.OK);

        var sections = (JsonArray)body!;
        sections.Should().ContainSingle();

        var section = (JsonObject)sections[0]!;
        section["key"]!.GetValue<string>().Should().Be("3");
        section["title"]!.GetValue<string>().Should().Be("Music");
        section["locations"]!.AsArray().Select(location => location!.GetValue<string>())
            .Should().Equal("/plex/music");

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Testing_the_connection_reports_a_working_server()
    {
        using var factory = Factory(Tv(), ServerClient());
        using var api = new Session(factory);
        await SignInAsync(api);
        await SelectServerAsync(api);

        var (status, body) = await api.PostAsync($"{Endpoint}/test");

        status.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)body!;
        result["ok"]!.GetValue<bool>().Should().BeTrue();
        result["serverName"]!.GetValue<string>().Should().Be("Living Room");
        result["version"]!.GetValue<string>().Should().Be(ServerVersion);
        result["musicSections"]!.GetValue<int>().Should().Be(1);
        result["error"].Should().BeNull();

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Testing_the_connection_reports_a_failure_instead_of_failing()
    {
        var server = ServerClient();
        server.GetSectionsAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PlexSection>>(
                new PlexException("The Plex server did not answer (HTTP 500).")));

        using var factory = Factory(Tv(), server);
        using var api = new Session(factory);
        await SignInAsync(api);
        await SelectServerAsync(api);

        var (status, body) = await api.PostAsync($"{Endpoint}/test");

        // The test endpoint answers 200 whatever happens: whether the server works is the body.
        status.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)body!;
        result["ok"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Be("The Plex server did not answer (HTTP 500).");

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Signing_out_forgets_the_sign_in_and_keeps_the_selected_server()
    {
        using var factory = Factory(Tv(), ServerClient());
        using var api = new Session(factory);
        await SignInAsync(api);
        await SelectServerAsync(api);

        var (signedOut, _) = await api.DeleteAsync(Endpoint);
        signedOut.Should().Be(HttpStatusCode.NoContent);

        var (status, body) = await api.GetAsync(Endpoint);

        status.Should().Be(HttpStatusCode.OK);

        var state = (JsonObject)body!;
        state["signedIn"]!.GetValue<bool>().Should().BeFalse();
        state["serverUrl"]!.GetValue<string>().Should().Be(ServerUrl);
        state["serverName"]!.GetValue<string>().Should().Be("Living Room");

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task Every_plex_endpoint_requires_a_key()
    {
        using var factory = Factory(Substitute.For<IPlexTvClient>(), Substitute.For<IPlexServerClient>());
        using var api = new Session(factory, authenticated: false);

        (await api.GetAsync(Endpoint)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.PostAsync($"{Endpoint}/pin")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.GetAsync($"{Endpoint}/pin/{PinId}")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.PutAsync($"{Endpoint}/token", new { token = Token })).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.GetAsync($"{Endpoint}/servers")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.PutAsync($"{Endpoint}/server", new { serverUrl = ServerUrl })).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.PostAsync($"{Endpoint}/test")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.GetAsync($"{Endpoint}/sections")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await api.DeleteAsync(Endpoint)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Listing_servers_without_a_sign_in_is_a_conflict()
    {
        using var factory = Factory(Substitute.For<IPlexTvClient>(), Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);

        var (status, body) = await api.GetAsync($"{Endpoint}/servers");

        status.Should().Be(HttpStatusCode.Conflict);
        Problem(body)["detail"]!.GetValue<string>().Should().Be("Not signed in to Plex");
    }

    [Fact]
    public async Task Asking_for_sections_without_a_selected_server_is_a_conflict()
    {
        using var factory = Factory(Tv(), Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);
        await SignInAsync(api);

        var (status, body) = await api.GetAsync($"{Endpoint}/sections");

        status.Should().Be(HttpStatusCode.Conflict);
        Problem(body)["detail"]!.GetValue<string>().Should().Be("No Plex server selected");
    }

    [Fact]
    public async Task Setting_an_empty_token_is_a_bad_request()
    {
        using var factory = Factory(Tv(), Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);

        var (status, _) = await api.PutAsync($"{Endpoint}/token", new { token = string.Empty });

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_token_plex_rejects_is_a_bad_request_that_does_not_echo_it()
    {
        var tv = Substitute.For<IPlexTvClient>();
        tv.GetServersAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PlexServer>>(
                new PlexUnauthorizedException("plex.tv answered 401.")));

        using var factory = Factory(tv, Substitute.For<IPlexServerClient>());
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync($"{Endpoint}/token", new { token = Token });

        status.Should().Be(HttpStatusCode.BadRequest);
        Problem(body)["detail"]!.GetValue<string>().Should().Be("Plex did not accept that token");

        api.AssertNoSecret(Token);
    }

    [Fact]
    public async Task A_server_url_that_is_not_a_url_is_a_bad_request()
    {
        using var factory = Factory(Tv(), ServerClient());
        using var api = new Session(factory);
        await SignInAsync(api);

        var (status, body) = await api.PutAsync($"{Endpoint}/server", new { serverUrl = "not a url" });

        status.Should().Be(HttpStatusCode.BadRequest);
        Problem(body)["detail"]!.GetValue<string>().Should().Contain("absolute");

        // A URL that carries a query is refused too, and the URL is not repeated back: it is what
        // would carry a credential.
        var (withQuery, queryBody) = await api.PutAsync(
            $"{Endpoint}/server",
            new { serverUrl = "https://plex.example:32400/?X-Plex-Token=secret-value" });

        withQuery.Should().Be(HttpStatusCode.BadRequest);
        api.AssertNoSecret("secret-value");
        queryBody!.ToJsonString().Should().NotContain("X-Plex-Token");
    }

    [Fact]
    public async Task An_unreachable_server_is_a_bad_gateway_carrying_plexs_reason()
    {
        var server = ServerClient();
        server.GetIdentityAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PlexIdentity>(new PlexException("The Plex server did not answer (HTTP 502).")));

        using var factory = Factory(Tv(), server);
        using var api = new Session(factory);
        await SignInAsync(api);

        var (status, body) = await api.PutAsync($"{Endpoint}/server", new { serverUrl = ServerUrl });

        status.Should().Be(HttpStatusCode.BadGateway);
        Problem(body)["detail"]!.GetValue<string>().Should().Be("The Plex server did not answer (HTTP 502).");

        api.AssertNoSecret(Token, ServerToken);
    }

    [Fact]
    public async Task A_stored_sign_in_plex_rejects_is_a_conflict()
    {
        var server = ServerClient();
        server.GetSectionsAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PlexSection>>(
                new PlexUnauthorizedException("The Plex server answered 401.")));

        using var factory = Factory(Tv(), server);
        using var api = new Session(factory);
        await SignInAsync(api);
        await SelectServerAsync(api);

        var (status, body) = await api.GetAsync($"{Endpoint}/sections");

        status.Should().Be(HttpStatusCode.Conflict);
        Problem(body)["detail"]!.GetValue<string>().Should().Be(PlexController.RejectedSignIn);

        api.AssertNoSecret(Token, ServerToken);
    }

    /// <summary>The API's client for one test, keeping every response body it saw.</summary>
    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;

        public Session(WondarrAppFactory factory, bool authenticated = true)
        {
            _client = factory.CreateClient();

            if (authenticated)
            {
                _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
            }
        }

        /// <summary>Every response body seen so far, so a test can prove none carried a token.</summary>
        public List<string> Bodies { get; } = [];

        public Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path) =>
            SendAsync(HttpMethod.Post, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> DeleteAsync(string path) =>
            SendAsync(HttpMethod.Delete, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> PutAsync(string path, object body) =>
            SendAsync(HttpMethod.Put, path, JsonSerializer.Serialize(body));

        /// <summary>Asserts that none of the responses so far contains any of <paramref name="secrets"/>.</summary>
        public void AssertNoSecret(params string[] secrets)
        {
            Bodies.Should().NotBeEmpty("the test has to have seen at least one response");

            foreach (var body in Bodies)
            {
                foreach (var secret in secrets)
                {
                    body.Should().NotContain(secret, "a Plex token must never leave the server");
                }
            }
        }

        public void Dispose() => _client.Dispose();

        private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(
            HttpMethod method,
            string path,
            string? json)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            Bodies.Add(raw);

            return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
        }
    }

    /// <summary>A factory whose plex.tv and Plex Media Server clients are the given fakes.</summary>
    private static WondarrAppFactory Factory(IPlexTvClient tv, IPlexServerClient server) =>
        new(configureServices: services =>
        {
            services.RemoveAll<IPlexTvClient>();
            services.RemoveAll<IPlexServerClient>();
            services.AddSingleton(tv);
            services.AddSingleton(server);
        });

    /// <summary>A plex.tv fake that accepts any token and knows one server.</summary>
    private static IPlexTvClient Tv()
    {
        var tv = Substitute.For<IPlexTvClient>();
        tv.GetServersAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([Server()]));

        return tv;
    }

    /// <summary>A Plex Media Server fake with one music section and one film section.</summary>
    private static IPlexServerClient ServerClient()
    {
        var server = Substitute.For<IPlexServerClient>();

        server.GetIdentityAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexIdentity(MachineIdentifier, ServerVersion, Claimed: true)));

        server.GetSectionsAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexSection>>([MusicSection(), FilmSection()]));

        return server;
    }

    private static PlexServer Server() => new(
        "Living Room",
        MachineIdentifier,
        Owned: true,
        AccessToken: ServerToken,
        ProductVersion: ServerVersion,
        [new PlexServerConnection(ServerUrl, Local: true, Relay: false, "https", "192-168-1-10.abc.plex.direct", 32400)]);

    private static PlexSection MusicSection() =>
        new("3", "Music", "artist", Refreshing: false, ["/plex/music"]);

    /// <summary>A section the API has to filter out: only music sections are offered.</summary>
    private static PlexSection FilmSection() =>
        new("1", "Films", "movie", Refreshing: false, ["/plex/films"]);

    /// <summary>Signs in with the pasted token, which the fake plex.tv accepts.</summary>
    private static async Task SignInAsync(Session api)
    {
        var (status, _) = await api.PutAsync($"{Endpoint}/token", new { token = Token });

        status.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task SelectServerAsync(Session api)
    {
        var (status, _) = await api.PutAsync($"{Endpoint}/server", new { serverUrl = ServerUrl });

        status.Should().Be(HttpStatusCode.OK);
    }

    private static JsonObject Problem(JsonNode? body) => (JsonObject)body!;
}
