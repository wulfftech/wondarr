using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Wondarr.Core.Notifications;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The notification endpoints: the CRUD round trip, the provider schema the editor is rendered from,
/// the test send, and the one thing that must never come back out of the API — a stored secret.
/// <para>
/// Sends go through a fake handler, except the failure case, which points at an address nothing
/// listens on. Nothing here touches the network.
/// </para>
/// </summary>
public sealed class NotificationApiTests
{
    private const string Endpoint = "/api/v1/notification";

    private static readonly string[] ImportAndFailure = ["import", "failure"];
    private static readonly string[] HealthOnly = ["health"];
    private static readonly string[] ImportOnly = ["import"];

    [Fact]
    public async Task A_notification_is_created_read_updated_and_deleted()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Hooks",
                implementation = "Webhook",
                enabled = true,
                events = ImportAndFailure,
                settings = new { url = "http://hooks.invalid/wondarr", method = "POST" },
            }));

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var row = await ReadJsonAsync(created);
        var id = row.GetProperty("id").GetInt64();

        row.GetProperty("name").GetString().Should().Be("Hooks");
        row.GetProperty("implementation").GetString().Should().Be("Webhook");
        row.GetProperty("enabled").GetBoolean().Should().BeTrue();
        row.GetProperty("events").EnumerateArray().Select(@event => @event.GetString())
            .Should().Equal("import", "failure");
        row.GetProperty("settings").GetProperty("url").GetString().Should().Be("http://hooks.invalid/wondarr");

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(fetched)).GetProperty("name").GetString().Should().Be("Hooks");

        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/{id}", UriKind.Relative),
            Body(new
            {
                name = "Hooks",
                implementation = "Webhook",
                enabled = false,
                events = HealthOnly,
                settings = new { url = "http://hooks.invalid/wondarr" },
            }));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await ReadJsonAsync(updated);
        changed.GetProperty("enabled").GetBoolean().Should().BeFalse();
        changed.GetProperty("events").EnumerateArray().Select(@event => @event.GetString())
            .Should().Equal("health");

        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var gone = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_page_of_notifications_comes_back_in_creation_order()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        await CreateAsync(client, "First");
        await CreateAsync(client, "Second");

        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await ReadJsonAsync(response);

        page.EnumerateArray().Select(row => row.GetProperty("name").GetString())
            .Should().Equal("First", "Second");
    }

    [Fact]
    public async Task The_schema_describes_the_Webhook_provider_and_its_fields()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = await ReadJsonAsync(response);
        var webhook = schema.EnumerateArray()
            .Single(entry => entry.GetProperty("implementation").GetString() == "Webhook");
        var fields = webhook.GetProperty("fields").EnumerateArray().ToList();

        fields.Select(field => field.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["url", "method", "username", "password", "headers"]);

        var url = Field(fields, "url");
        url.GetProperty("required").GetBoolean().Should().BeTrue();
        url.GetProperty("type").GetString().Should().Be("url");

        var method = Field(fields, "method");
        method.GetProperty("options").EnumerateArray().Select(option => option.GetString())
            .Should().Equal("POST", "PUT");

        Field(fields, "password").GetProperty("secret").GetBoolean().Should().BeTrue();
        Field(fields, "username").GetProperty("secret").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_stored_secret_never_comes_back_out()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        var id = await CreateAsync(client, "Hooks", password: "wonderwall");

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        var row = await ReadJsonAsync(fetched);
        var settings = row.GetProperty("settings");

        settings.GetProperty("password").GetString().Should().Be("********");
        settings.GetProperty("password").GetString().Should().NotBe("wonderwall");

        using var page = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        var listed = await ReadJsonAsync(page);

        listed[0].GetProperty("settings").GetProperty("password").GetString().Should().Be("********");
    }

    [Fact]
    public async Task The_schema_lists_the_Webhook_Discord_and_Apprise_providers()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = await ReadJsonAsync(response);

        schema.EnumerateArray().Select(entry => entry.GetProperty("implementation").GetString())
            .Should().BeEquivalentTo(["Webhook", "Discord", "Apprise"]);

        var discord = Field(
            schema.EnumerateArray().Single(entry => entry.GetProperty("implementation").GetString() == "Discord")
                .GetProperty("fields").EnumerateArray().ToList(),
            "webHookUrl");

        discord.GetProperty("type").GetString().Should().Be("url");
        discord.GetProperty("required").GetBoolean().Should().BeTrue();
        discord.GetProperty("secret").GetBoolean().Should().BeTrue();

        var apprise = Field(
            schema.EnumerateArray().Single(entry => entry.GetProperty("implementation").GetString() == "Apprise")
                .GetProperty("fields").EnumerateArray().ToList(),
            "statelessUrls");

        apprise.GetProperty("secret").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_Discord_webhook_url_never_comes_back_out()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Discord",
                implementation = "Discord",
                enabled = true,
                events = ImportOnly,
                settings = new { webHookUrl = "https://discord.com/api/webhooks/123/token" },
            }));

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        var settings = (await ReadJsonAsync(fetched)).GetProperty("settings");

        settings.GetProperty("webHookUrl").GetString().Should().Be("********");
        (await fetched.Content.ReadAsStringAsync()).Should().NotContain("/token");
    }

    [Fact]
    public async Task A_test_send_reaches_the_endpoint_with_the_Test_event()
    {
        var handler = new RecordingHandler();
        using var factory = Factory(handler);
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "Hooks",
                implementation = "Webhook",
                enabled = true,
                settings = new { url = "http://hooks.invalid/wondarr" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{}");

        var body = JsonSerializer.Deserialize<JsonElement>(handler.Bodies.Single());

        body.GetProperty("eventType").GetString().Should().Be("Test");
        body.GetProperty("instanceName").GetString().Should().Be("Wondarr");
        body.GetProperty("song").GetProperty("title").GetString().Should().Be("Test Title");
    }

    [Fact]
    public async Task A_test_send_that_cannot_reach_the_endpoint_reports_it_without_the_address()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "Hooks",
                implementation = "Webhook",
                enabled = true,
                settings = new { url = "http://127.0.0.1:9/wondarr" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("settings")[0].GetString();

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task A_settings_value_the_provider_refuses_is_a_validation_problem()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Hooks",
                implementation = "Webhook",
                enabled = true,
                settings = new { url = "ftp://hooks.invalid" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("settings")[0].GetString();

        message.Should().Contain("http");
    }

    [Fact]
    public async Task The_notification_endpoints_require_a_key()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        using var schema = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));
        using var test = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new { name = "Hooks", implementation = "Webhook" }));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        schema.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        test.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A host whose notification client sends through <paramref name="handler"/>. Post-configured
    /// because the app registers the same named client, and the last configuration to run wins.
    /// </summary>
    private static WondarrAppFactory Factory(HttpMessageHandler? handler = null) =>
        new(configureServices: services =>
        {
            if (handler is null)
            {
                return;
            }

            services.PostConfigure<HttpClientFactoryOptions>(NotificationHttp.ClientName, options =>
            {
                options.HttpMessageHandlerBuilderActions.Clear();
                options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler);
            });
        });

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<long> CreateAsync(HttpClient client, string name, string password = "")
    {
        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name,
                implementation = "Webhook",
                enabled = true,
                events = ImportOnly,
                settings = new { url = "http://hooks.invalid/wondarr", password },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static JsonElement Field(IReadOnlyList<JsonElement> fields, string name) =>
        fields.Single(field => field.GetProperty("name").GetString() == name);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>Answers every send with <c>200</c> and remembers the body, so no socket is opened.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];

        /// <summary>What the endpoint was sent, in order.</summary>
        public IReadOnlyList<string> Bodies => _bodies;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            _bodies.Add(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
