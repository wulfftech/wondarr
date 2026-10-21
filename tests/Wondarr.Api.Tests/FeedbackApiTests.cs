using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Feedback;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The feedback endpoints, with GitHub stubbed at the HTTP layer so no test reaches the network.</summary>
public sealed class FeedbackApiTests
{
    private const string Endpoint = "/api/v1/feedback";

    private const string Token = "ghp_feedbackapitoken0123456789";

    [Fact]
    public async Task The_feedback_endpoints_need_the_api_key()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var client = factory.CreateClient();

        using var status = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        status.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var post = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            new StringContent("""{"kind":"bug","text":"x"}""", Encoding.UTF8, "application/json"));
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var settings = await client.GetAsync(new Uri($"{Endpoint}/settings", UriKind.Relative));
        settings.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var put = await client.PutAsync(
            new Uri($"{Endpoint}/settings", UriKind.Relative),
            new StringContent($$"""{"token":"{{Token}}"}""", Encoding.UTF8, "application/json"));
        put.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var test = await client.PostAsync(
            new Uri($"{Endpoint}/settings/test", UriKind.Relative),
            new StringContent($$"""{"token":"{{Token}}"}""", Encoding.UTF8, "application/json"));
        test.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_token_a_submission_returns_the_prefilled_form_and_asks_github_nothing()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (_, read) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)read!)["autoRaise"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)read)["tokenConfigured"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)read)["repository"]!.GetValue<string>().Should().Be("wulfftech/wondarr");
        ((JsonObject)read)["maxTextLength"]!.GetValue<int>().Should().Be(FeedbackService.MaxTextLength);

        var (status, body) = await api.SendAsync(
            HttpMethod.Post,
            Endpoint,
            """{"kind":"idea","text":"Sort the queue by size","page":"/activity/queue","pageTitle":"Queue"}""");

        status.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)body!;

        result["raised"]!.GetValue<bool>().Should().BeFalse();
        result["issueNumber"].Should().BeNull();
        result["reason"].Should().BeNull();
        result["mayBeRaised"]!.GetValue<bool>().Should().BeFalse();
        result["issueListUrl"]!.GetValue<string>().Should().Be("https://github.com/wulfftech/wondarr/issues");

        var url = result["issueUrl"]!.GetValue<string>();

        url.Should().StartWith("https://github.com/wulfftech/wondarr/issues/new?title=Sort%20the%20queue%20by%20size&labels=enhancement&body=");
        Uri.UnescapeDataString(url).Should().Contain("- **Page:** `/activity/queue` (`Queue`)").And.Contain("- **Wondarr:** 0.1.9");
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task With_a_token_a_submission_opens_the_issue()
    {
        using var github = new Github
        {
            Status = HttpStatusCode.Created,
            Answer = """{"number":12,"html_url":"https://github.com/wulfftech/wondarr/issues/12"}""",
        };
        using var factory = Factory(github, token: Token);
        using var api = new Session(factory);

        var (_, read) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)read!)["autoRaise"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)read)["tokenConfigured"]!.GetValue<bool>().Should().BeTrue();

        var (status, body) = await api.SendAsync(HttpMethod.Post, Endpoint, """{"kind":"bug","text":"The logs page is empty","page":"/system/logs"}""");

        status.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)body!;

        result["raised"]!.GetValue<bool>().Should().BeTrue();
        result["issueNumber"]!.GetValue<int>().Should().Be(12);
        result["issueUrl"]!.GetValue<string>().Should().Be("https://github.com/wulfftech/wondarr/issues/12");

        github.Requests.Should().Be(1);
        github.LastUri.Should().Be(FeedbackService.IssuesUrl);
        github.LastAuthorization.Should().Be($"Bearer {Token}");
        github.LastUserAgent.Should().StartWith("Wondarr/0.1.9");
        JsonNode.Parse(github.LastBody!)!["labels"]!.AsArray().Select(label => label!.GetValue<string>()).Should().Equal("bug");
    }

    [Fact]
    public async Task A_refused_token_still_answers_200_with_the_form_and_the_reason()
    {
        using var github = new Github { Status = HttpStatusCode.Unauthorized };
        using var factory = Factory(github, token: Token);
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Post, Endpoint, """{"kind":"bug","text":"Broken"}""");

        status.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)body!;

        result["raised"]!.GetValue<bool>().Should().BeFalse();
        result["mayBeRaised"]!.GetValue<bool>().Should().BeFalse();
        result["reason"]!.GetValue<string>().Should().Contain("401");
        result["issueUrl"]!.GetValue<string>().Should().StartWith(FeedbackService.NewIssueUrl);

        // Through the app's own client pipeline: a retry handler added to it would show here.
        github.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"kind":"bug"}""")]
    [InlineData("""{"kind":"bug","text":"   "}""")]
    public async Task A_blank_text_is_refused(string json)
    {
        using var github = new Github();
        using var factory = Factory(github, token: Token);
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Post, Endpoint, json);

        status.Should().Be(HttpStatusCode.BadRequest);
        ((JsonObject)body!)["errors"]!["text"].Should().NotBeNull();
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_text_over_the_limit_is_refused()
    {
        using var github = new Github();
        using var factory = Factory(github, token: Token);
        using var api = new Session(factory);

        var json = new JsonObject { ["kind"] = "bug", ["text"] = new string('a', FeedbackService.MaxTextLength + 1) }.ToJsonString();

        var (status, _) = await api.SendAsync(HttpMethod.Post, Endpoint, json);

        status.Should().Be(HttpStatusCode.BadRequest);
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_kind_is_refused()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (status, _) = await api.SendAsync(HttpMethod.Post, Endpoint, """{"kind":"rant","text":"x"}""");

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_token_saved_on_the_settings_card_turns_the_button_on_and_is_never_read_back()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (_, before) = await api.SendAsync(HttpMethod.Get, $"{Endpoint}/settings");

        ((JsonObject)before!)["tokenSet"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)before)["tokenLocked"]!.GetValue<bool>().Should().BeFalse();

        var (status, saved) = await api.SendAsync(HttpMethod.Put, $"{Endpoint}/settings", $$"""{"token":"  {{Token}} "}""");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)saved!)["tokenSet"]!.GetValue<bool>().Should().BeTrue();
        saved.ToJsonString().Should().NotContain(Token);

        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("feedback:").And.Contain($"github_token: \"{Token}\"");

        // The file is reloaded on write: the header's status follows without a restart.
        var (_, status2) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)status2!)["tokenConfigured"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)status2)["autoRaise"]!.GetValue<bool>().Should().BeTrue();

        var (_, removed) = await api.SendAsync(HttpMethod.Put, $"{Endpoint}/settings", """{"token":""}""");

        ((JsonObject)removed!)["tokenSet"]!.GetValue<bool>().Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"))).Should().NotContain(Token);

        var (_, status3) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)status3!)["tokenConfigured"]!.GetValue<bool>().Should().BeFalse();
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Something_that_cannot_be_a_token_is_refused_on_the_token_field()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Put, $"{Endpoint}/settings", """{"token":"Bearer ghp_abc"}""");

        status.Should().Be(HttpStatusCode.BadRequest);
        ((JsonObject)body!)["errors"]!["token"].Should().NotBeNull();
    }

    [Fact]
    public async Task The_test_checks_a_typed_token_with_github_and_opens_nothing()
    {
        using var github = new Github
        {
            Status = HttpStatusCode.OK,
            Answer = """{"full_name":"wulfftech/wondarr"}""",
            Headers = new Dictionary<string, string> { ["X-OAuth-Scopes"] = "public_repo" },
        };
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/settings/test", $$"""{"token":"{{Token}}"}""");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["ok"]!.GetValue<bool>().Should().BeTrue();
        body.ToJsonString().Should().NotContain(Token);
        github.Requests.Should().Be(1);
        github.LastUri.Should().Be(FeedbackService.RepositoryApiUrl);
        github.LastAuthorization.Should().Be($"Bearer {Token}");
    }

    [Fact]
    public async Task The_test_without_any_token_says_so_and_asks_github_nothing()
    {
        using var github = new Github();
        using var factory = Factory(github);
        using var api = new Session(factory);

        var (_, body) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/settings/test", "{}");

        ((JsonObject)body!)["ok"]!.GetValue<bool>().Should().BeFalse();
        github.Requests.Should().Be(0);
    }

    internal static WondarrAppFactory Factory(Github github, string? token = null) =>
        new(
            settings: token is null ? null : new Dictionary<string, string?> { ["Feedback:GitHubToken"] = token },
            configureServices: services =>
            {
                services.AddHttpClient(FeedbackService.ClientName).ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddSingleton(RunningVersion.Parse("0.1.9"));
            });

    /// <summary>A fake GitHub: answers every request with one status, body and headers, and remembers the last request.</summary>
    internal sealed class Github : HttpMessageHandler
    {
        private int _requests;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.Created;

        public string Answer { get; init; } = "{}";

        public Dictionary<string, string> Headers { get; init; } = [];

        public int Requests => Volatile.Read(ref _requests);

        public string? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        public string? LastUserAgent { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            LastUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUserAgent = request.Headers.UserAgent.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Answer, Encoding.UTF8, "application/json"),
            };

            foreach (var (name, value) in Headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }

    internal sealed class Session : IDisposable
    {
        private readonly HttpClient _client;

        public Session(WondarrAppFactory factory)
        {
            _client = factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        }

        public void Dispose() => _client.Dispose();

        public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
        }
    }
}

/// <summary>
/// Not parallelised: the test sets a real <c>APP__FEEDBACK__GITHUB_TOKEN</c> variable for the duration of its
/// run, and a concurrently starting host would read it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FeedbackEnvironmentTestsSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "Feedback environment";
}

/// <summary>A token the environment sets is read-only on the settings card.</summary>
[Collection(FeedbackEnvironmentTestsSeries.Name)]
public sealed class FeedbackEnvironmentTests
{
    [Fact]
    public async Task A_token_the_environment_sets_is_locked_and_a_change_is_refused_with_the_variable_named()
    {
        const string Token = "ghp_environmenttoken0123456789";

        Environment.SetEnvironmentVariable("APP__FEEDBACK__GITHUB_TOKEN", Token);

        try
        {
            using var github = new FeedbackApiTests.Github();
            using var factory = FeedbackApiTests.Factory(github);
            using var api = new FeedbackApiTests.Session(factory);

            var (_, read) = await api.SendAsync(HttpMethod.Get, "/api/v1/feedback/settings");

            ((JsonObject)read!)["tokenSet"]!.GetValue<bool>().Should().BeTrue();
            ((JsonObject)read)["tokenLocked"]!.GetValue<bool>().Should().BeTrue();

            var (_, status) = await api.SendAsync(HttpMethod.Get, "/api/v1/feedback");

            ((JsonObject)status!)["tokenConfigured"]!.GetValue<bool>().Should().BeTrue();

            var (code, body) = await api.SendAsync(HttpMethod.Put, "/api/v1/feedback/settings", """{"token":"ghp_other0123456789"}""");

            code.Should().Be(HttpStatusCode.BadRequest);
            ((JsonObject)body!)["errors"]!["token"]!.AsArray()
                .Should().Contain(message => message!.GetValue<string>().Contains("APP__FEEDBACK__GITHUB_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__FEEDBACK__GITHUB_TOKEN", null);
        }
    }
}
