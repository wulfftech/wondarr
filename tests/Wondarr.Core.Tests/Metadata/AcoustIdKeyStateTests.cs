using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Tests.Media;
using Xunit;

namespace Wondarr.Core.Tests.Metadata;

/// <summary>
/// A rejected AcoustID client key (code review 2026-10-10, owner question 17): the lookup tells the
/// key state, the Health page reads it as an error that names AcoustID and the fix, and the error
/// clears once a lookup succeeds or the key is changed.
/// </summary>
public sealed class AcoustIdKeyStateTests
{
    private const string BadKeyBody = """{"status":"error","error":{"code":4,"message":"invalid API key"}}""";

    private const string GoodBody = """{"status":"ok","results":[]}""";

    [Fact]
    public async Task A_rejected_key_is_a_health_error_that_names_acoustid_and_the_fix()
    {
        var (client, check, _) = Create(new AcoustIdOptions { ClientKey = "bad-key" }, BadKeyBody);

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok, "nothing has been tried yet");

        var lookup = await client.LookupAsync("AQAAAA", 200, CancellationToken.None);
        lookup.Status.Should().Be(AcoustIdStatus.InvalidKey);

        var report = await check.CheckAsync(CancellationToken.None);
        report.Type.Should().Be(HealthCheckResult.Error);
        report.Message.Should().Contain("AcoustID").And.Contain("Settings").And.Contain("Metadata services");
        report.Message.Should().NotContain("bad-key", "a key is never echoed");
    }

    [Fact]
    public async Task The_error_clears_when_a_lookup_succeeds()
    {
        var handler = new ScriptedHandler(BadKeyBody);
        var (client, check, _) = Create(new AcoustIdOptions { ClientKey = "key" }, handler);

        await client.LookupAsync("AQAAAA", 200, CancellationToken.None);
        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Error);

        handler.Body = GoodBody;
        await client.LookupAsync("AQAAAA", 200, CancellationToken.None);

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task The_error_clears_when_the_key_is_changed()
    {
        var options = new AcoustIdOptions { ClientKey = "old-key" };
        var (client, check, _) = Create(options, BadKeyBody);

        await client.LookupAsync("AQAAAA", 200, CancellationToken.None);
        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Error);

        // The user pastes a new key: it has not been refused, so it is not reported as refused.
        options.ClientKey = "new-key";

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public void Deferrals_are_counted_per_item_and_forgotten_on_request()
    {
        var state = new AcoustIdKeyState();

        state.CountDeferral(1).Should().Be(1);
        state.CountDeferral(1).Should().Be(2);
        state.CountDeferral(2).Should().Be(1);

        state.ForgetDeferrals(1);
        state.CountDeferral(1).Should().Be(1);

        state.Accepted();
        state.CountDeferral(2).Should().Be(1, "a key that works again starts every wait over");
    }

    private static (AcoustIdClient Client, AcoustIdKeyHealthCheck Check, AcoustIdKeyState State) Create(
        AcoustIdOptions options,
        string body) => Create(options, new ScriptedHandler(body));

    private static (AcoustIdClient Client, AcoustIdKeyHealthCheck Check, AcoustIdKeyState State) Create(
        AcoustIdOptions options,
        ScriptedHandler handler)
    {
        var state = new AcoustIdKeyState();
        var monitor = new TestOptionsMonitor<AcoustIdOptions>(options);

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.acoustid.org/v2/") };
        var client = new AcoustIdClient(http, monitor, TimeProvider.System, NullLogger<AcoustIdClient>.Instance, state);

        return (client, new AcoustIdKeyHealthCheck(state, monitor), state);
    }

    private sealed class ScriptedHandler(string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
    }
}
