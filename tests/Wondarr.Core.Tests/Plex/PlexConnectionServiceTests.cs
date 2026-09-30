using Wondarr.Core.Plex;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexConnectionServiceTests
{
    private const string UserToken = "PLEX-USER-TOKEN";
    private const string ServerToken = "PLEX-SERVER-TOKEN";
    private const string MachineIdentifier = "0000000000000000000000000000000000000000";

    [Fact]
    public async Task Generates_the_client_identifier_once_and_keeps_it_across_a_sign_out()
    {
        using var harness = new PlexConnectionHarness();
        var service = harness.CreateService();

        var first = await service.GetStateAsync(CancellationToken.None);

        first.ClientIdentifier.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");

        await service.SignOutAsync(CancellationToken.None);

        var after = await service.GetStateAsync(CancellationToken.None);

        after.ClientIdentifier.Should().Be(first.ClientIdentifier);
    }

    [Fact]
    public async Task CompleteSignIn_stores_the_token_and_never_returns_it()
    {
        using var harness = new PlexConnectionHarness();
        harness.Tv.CheckPinAsync(1234567890, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: UserToken)));

        var service = harness.CreateService();

        var status = await service.CompleteSignInAsync(1234567890, CancellationToken.None);

        status.AuthToken.Should().BeNull();
        status.Expired.Should().BeFalse();

        (await harness.ReadSettingsAsync())!.Token.Should().Be(UserToken);
        harness.Secrets.Redact($"token={UserToken}").Should().NotContain(UserToken);

        // And the state the UI reads carries no token either.
        var state = await service.GetStateAsync(CancellationToken.None);
        state.SignedIn.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteSignIn_stores_nothing_while_the_PIN_is_still_pending()
    {
        using var harness = new PlexConnectionHarness();
        harness.Tv.CheckPinAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: null)));

        var service = harness.CreateService();

        await service.CompleteSignInAsync(1234567890, CancellationToken.None);

        (await harness.ReadSettingsAsync())!.Token.Should().BeNull();
        (await service.GetStateAsync(CancellationToken.None)).SignedIn.Should().BeFalse();
    }

    [Fact]
    public async Task StartSignIn_uses_the_stored_client_identifier()
    {
        using var harness = new PlexConnectionHarness();
        harness.Tv.CreatePinAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPin(1, "code", DateTimeOffset.MaxValue, "https://app.plex.tv/auth")));

        var service = harness.CreateService();

        var pin = await service.StartSignInAsync(CancellationToken.None);
        var state = await service.GetStateAsync(CancellationToken.None);

        pin.Code.Should().Be("code");
        await harness.Tv.Received(1).CreatePinAsync(state.ClientIdentifier, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetToken_stores_a_token_plex_tv_accepts()
    {
        using var harness = new PlexConnectionHarness();
        harness.Tv.GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([]));

        var service = harness.CreateService();
        await service.SetTokenAsync($"  {UserToken}  ", CancellationToken.None);

        (await harness.ReadSettingsAsync())!.Token.Should().Be(UserToken);
        harness.Secrets.Redact(UserToken).Should().Be("(removed)");
    }

    [Fact]
    public async Task SetToken_rethrows_when_plex_tv_refuses_the_token_and_stores_nothing()
    {
        using var harness = new PlexConnectionHarness();
        harness.Tv.GetServersAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PlexServer>>(
                new PlexUnauthorizedException("plex.tv refused GET /api/v2/resources with 401")));

        var service = harness.CreateService();

        var act = async () => await service.SetTokenAsync(UserToken, CancellationToken.None);

        (await act.Should().ThrowAsync<PlexUnauthorizedException>())
            .Which.Message.Should().NotContain(UserToken);

        (await harness.ReadSettingsAsync())!.Token.Should().BeNull();
    }

    [Fact]
    public async Task GetServers_requires_a_token()
    {
        using var harness = new PlexConnectionHarness();
        var service = harness.CreateService();

        var act = async () => await service.GetServersAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Not signed in to Plex");
    }

    [Fact]
    public async Task GetServers_blanks_every_access_token()
    {
        using var harness = new PlexConnectionHarness();
        await SignInAsync(harness);
        harness.Tv.GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([
                new PlexServer("Test Server 0", MachineIdentifier, true, ServerToken, "1.43.4", []),
            ]));

        var servers = await harness.CreateService().GetServersAsync(CancellationToken.None);

        servers.Should().ContainSingle();
        servers[0].AccessToken.Should().BeNull();
        servers[0].Name.Should().Be("Test Server 0");
    }

    [Fact]
    public async Task SelectServer_uses_the_resource_token_of_the_matching_machine_identifier()
    {
        using var harness = new PlexConnectionHarness();
        await SignInAsync(harness);
        StubIdentity(harness);
        harness.Tv.GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([
                new PlexServer("Another Server", "1111111111111111111111111111111111111111", true, "OTHER-TOKEN", "1.0", []),
                new PlexServer("Test Server 0", MachineIdentifier, true, ServerToken, "1.43.4", []),
            ]));

        var service = harness.CreateService();
        var identity = await service.SelectServerAsync("https://plex.example:32400", CancellationToken.None);

        identity.MachineIdentifier.Should().Be(MachineIdentifier);

        var settings = (await harness.ReadSettingsAsync())!;
        settings.ServerUrl.Should().Be("https://plex.example:32400");
        settings.ServerName.Should().Be("Test Server 0");
        settings.ServerToken.Should().Be(ServerToken);

        // The context later tasks use carries the server's own token.
        var context = await service.GetServerContextAsync(CancellationToken.None);
        context.Should().NotBeNull();
        context!.Value.Token.Should().Be(ServerToken);
        context.Value.Server.Should().Be(new Uri("https://plex.example:32400"));

        await harness.Tv.Received().GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectServer_falls_back_to_the_user_token_when_plex_tv_does_not_know_the_server()
    {
        using var harness = new PlexConnectionHarness();
        await SignInAsync(harness);
        StubIdentity(harness);
        harness.Tv.GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([]));

        var service = harness.CreateService();
        await service.SelectServerAsync("https://plex.example:32400", CancellationToken.None);

        var settings = (await harness.ReadSettingsAsync())!;
        settings.ServerToken.Should().Be(UserToken);
        settings.ServerName.Should().Be(MachineIdentifier);
    }

    [Theory]
    [InlineData("plex.example:32400")]
    [InlineData("/plex")]
    [InlineData("ftp://plex.example")]
    public async Task SelectServer_rejects_a_url_that_is_not_absolute_http_or_https(string url)
    {
        using var harness = new PlexConnectionHarness();
        await SignInAsync(harness);

        var act = async () => await harness.CreateService().SelectServerAsync(url, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Test_reports_a_failure_instead_of_throwing()
    {
        using var harness = new PlexConnectionHarness();
        await SelectServerAsync(harness);
        harness.Server.GetIdentityAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PlexIdentity>(new PlexException("The Plex server did not answer within 15 seconds.")));

        var result = await harness.CreateService().TestAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("did not answer").And.NotContain(UserToken);
        result.ServerName.Should().BeNull();
    }

    [Fact]
    public async Task Test_reports_the_server_the_version_and_the_music_section_count()
    {
        using var harness = new PlexConnectionHarness();
        await SelectServerAsync(harness);
        harness.Server.GetIdentityAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexIdentity(MachineIdentifier, "1.43.4.10903-e5521bd8c", true)));
        harness.Server.GetSectionsAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexSection>>([
                new PlexSection("1", "Movies", "movie", false, []),
                new PlexSection("5", "Music", "artist", false, ["/data/artist3"]),
                new PlexSection("11", "Music 2", "artist", false, ["/data/artist2"]),
            ]));

        var result = await harness.CreateService().TestAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.ServerName.Should().Be("Test Server 0");
        result.Version.Should().Be("1.43.4.10903-e5521bd8c");
        result.MusicSections.Should().Be(2);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Test_reports_when_no_server_is_selected()
    {
        using var harness = new PlexConnectionHarness();
        await SignInAsync(harness);

        var result = await harness.CreateService().TestAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("No Plex server selected");
    }

    [Fact]
    public async Task GetMusicSections_returns_only_the_artist_sections_of_the_selected_server()
    {
        using var harness = new PlexConnectionHarness();
        await SelectServerAsync(harness);
        harness.Server.GetSectionsAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexSection>>([
                new PlexSection("1", "Movies", "movie", false, []),
                new PlexSection("5", "Music", "artist", false, ["/data/artist3"]),
            ]));

        var sections = await harness.CreateService().GetMusicSectionsAsync(CancellationToken.None);

        sections.Should().ContainSingle();
        sections[0].Key.Should().Be("5");

        await harness.Server.Received().GetSectionsAsync(
            new Uri("https://plex.example:32400"),
            ServerToken,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignOut_clears_both_tokens_and_keeps_the_selected_server()
    {
        using var harness = new PlexConnectionHarness();
        await SelectServerAsync(harness);

        var service = harness.CreateService();
        await service.SignOutAsync(CancellationToken.None);

        var settings = (await harness.ReadSettingsAsync())!;
        settings.Token.Should().BeNull();
        settings.ServerToken.Should().BeNull();
        settings.ServerUrl.Should().Be("https://plex.example:32400");
        settings.ServerName.Should().Be("Test Server 0");
        settings.ClientIdentifier.Should().NotBeNullOrEmpty();

        (await service.GetServerContextAsync(CancellationToken.None)).Should().BeNull();
    }

    private static Task<PlexPinStatus> SignInAsync(PlexConnectionHarness harness)
    {
        harness.Tv.CheckPinAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexPinStatus(Expired: false, AuthToken: UserToken)));

        return harness.CreateService().CompleteSignInAsync(1234567890, CancellationToken.None);
    }

    private static void StubIdentity(PlexConnectionHarness harness) =>
        harness.Server.GetIdentityAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlexIdentity(MachineIdentifier, "1.43.4.10903-e5521bd8c", true)));

    private static async Task SelectServerAsync(PlexConnectionHarness harness)
    {
        await SignInAsync(harness);
        StubIdentity(harness);

        harness.Tv.GetServersAsync(UserToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlexServer>>([
                new PlexServer("Test Server 0", MachineIdentifier, true, ServerToken, "1.43.4", []),
            ]));

        await harness.CreateService().SelectServerAsync("https://plex.example:32400", CancellationToken.None);
    }
}
