using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Feedback;
using Wondarr.Core.Logging;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Core.Tests.Feedback;

/// <summary>The feedback service against a fake GitHub (a handler) and a fake clock.</summary>
public sealed class FeedbackServiceTests : IDisposable
{
    private const string Token = "ghp_feedbacktoken0123456789";

    private static readonly CancellationToken None = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly FeedbackOptions _options = new();
    private readonly SecretRegistry _secrets = new();

    public void Dispose() => _github.Dispose();

    [Fact]
    public async Task Without_a_token_it_asks_nothing_and_returns_the_prefilled_form()
    {
        var outcome = await Service().SubmitAsync(
            new FeedbackSubmission(FeedbackKind.Bug, "  The Wanted page never loads\n\nIt spins forever.  ", "/wanted?page=2", "Wanted"),
            None);

        outcome.Raised.Should().BeFalse();
        outcome.Reason.Should().BeNull();
        outcome.IssueNumber.Should().BeNull();
        _github.Requests.Should().BeEmpty();

        var form = Query(outcome.IssueUrl);

        outcome.IssueUrl.Should().StartWith("https://github.com/wulfftech/wondarr/issues/new?");
        form["title"].Should().Be("The Wanted page never loads");
        form["labels"].Should().Be("bug");
        form["body"].Should().StartWith("The Wanted page never loads\n\nIt spins forever.\n\n")
            .And.Contain("- **Kind:** Bug")
            .And.Contain("- **Page:** `/wanted?page=2` (`Wanted`)")
            .And.Contain("- **Wondarr:** 0.1.9")
            .And.NotContain("Cut short");
    }

    [Fact]
    public async Task An_idea_is_labelled_enhancement()
    {
        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Idea, "Dark mode for the logs", null, null), None);

        var form = Query(outcome.IssueUrl);

        form["labels"].Should().Be("enhancement");
        form["body"].Should().Contain("- **Kind:** Idea").And.NotContain("**Page:**");
    }

    [Fact]
    public async Task Every_submission_is_logged_with_its_page_and_text()
    {
        var logger = new RecordingLogger();

        await Service(logger: logger).SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "Broken thing", "/library", "Library"), None);

        logger.Messages.Should().ContainSingle(message => message.Contains("Broken thing") && message.Contains("/library"));
    }

    [Fact]
    public async Task A_page_that_is_not_a_path_is_dropped_and_a_messy_one_is_put_on_one_line()
    {
        var dropped = await Service().SubmitAsync(
            new FeedbackSubmission(FeedbackKind.Bug, "x", "https://evil.example/", "Title"),
            None);

        Query(dropped.IssueUrl)["body"].Should().NotContain("**Page:**");

        var messy = await Service().SubmitAsync(
            new FeedbackSubmission(FeedbackKind.Bug, "x", "/song/`12`\n", "Song\r\n  page"),
            None);

        Query(messy.IssueUrl)["body"].Should().Contain("- **Page:** `/song/12` (`Song page`)");
    }

    [Fact]
    public async Task The_title_is_the_first_line_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 40)) + "\nsecond line";

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, null, null), None);

        var title = Query(outcome.IssueUrl)["title"];

        title.Should().EndWith("word…");
        title.Length.Should().BeLessThanOrEqualTo(FeedbackService.MaxTitleLength);
    }

    [Fact]
    public async Task Text_of_only_control_characters_gets_the_default_title()
    {
        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "\u0001\u0002", null, null), None);

        Query(outcome.IssueUrl)["title"].Should().Be(FeedbackService.DefaultTitle);
    }

    [Fact]
    public async Task A_text_too_long_for_the_form_link_keeps_the_longest_part_that_fits_and_says_so()
    {
        // Three bytes in UTF-8 each, so nine characters once escaped: 4000 of them are far over the limit.
        var text = new string('漢', FeedbackService.MaxTextLength);

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, "/library", "Library"), None);

        // One more character would add nine, so a link shorter than this left room for one.
        outcome.IssueUrl.Length.Should().BeInRange(FeedbackService.MaxFormUrlLength - 8, FeedbackService.MaxFormUrlLength);
        Query(outcome.IssueUrl)["body"].Should().Contain("Cut short").And.Contain("- **Page:** `/library`");
    }

    [Fact]
    public async Task A_long_text_that_fits_the_form_link_is_not_cut()
    {
        var text = new string('a', FeedbackService.MaxTextLength);

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, "/library", "Library"), None);

        outcome.IssueUrl.Length.Should().BeLessThanOrEqualTo(FeedbackService.MaxFormUrlLength);
        Query(outcome.IssueUrl)["body"].Should().StartWith(text).And.NotContain("Cut short");
    }

    [Fact]
    public async Task A_cut_never_splits_an_emoji()
    {
        var text = string.Concat(Enumerable.Repeat("😀", FeedbackService.MaxTextLength / 2));

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, null, null), None);

        var body = Query(outcome.IssueUrl)["body"];

        body.Should().Contain("Cut short");
        body.Should().NotContain("�");
    }

    [Fact]
    public async Task With_a_token_it_opens_the_issue_and_returns_its_page()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.Created, """{"number":42,"html_url":"https://github.com/wulfftech/wondarr/issues/42"}""");

        var outcome = await Service().SubmitAsync(
            new FeedbackSubmission(FeedbackKind.Idea, "Show the bitrate in the queue", "/activity", "Activity"),
            None);

        outcome.Should().Be(new FeedbackOutcome(true, "https://github.com/wulfftech/wondarr/issues/42", 42, null));

        var request = _github.Requests.Should().ContainSingle().Subject;

        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(FeedbackService.IssuesUrl);
        request.Authorization.Should().Be($"Bearer {Token}");

        var body = JsonNode.Parse(request.Body)!.AsObject();

        body["title"]!.GetValue<string>().Should().Be("Show the bitrate in the queue");
        body["body"]!.GetValue<string>().Should().Contain("- **Page:** `/activity` (`Activity`)");
        body["labels"]!.AsArray().Select(label => label!.GetValue<string>()).Should().Equal("enhancement");
    }

    [Fact]
    public async Task A_created_issue_whose_page_is_not_on_github_links_to_the_issue_list()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.Created, """{"number":7,"html_url":"https://evil.example/7"}""");

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "x", null, null), None);

        outcome.Raised.Should().BeTrue();
        outcome.IssueUrl.Should().Be("https://github.com/wulfftech/wondarr/issues");
        outcome.IssueNumber.Should().Be(7);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "422")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task A_refusal_falls_back_to_the_form_with_the_reason(HttpStatusCode status, string code)
    {
        _options.GitHubToken = Token;
        _github.Respond(status, "{}");

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "Broken", null, null), None);

        outcome.Raised.Should().BeFalse();
        outcome.MayBeRaised.Should().BeFalse();
        outcome.Reason.Should().Contain(code);
        outcome.IssueUrl.Should().StartWith(FeedbackService.NewIssueUrl);
        _github.Requests.Should().HaveCount(1, "a refused POST is never sent again");
    }

    [Fact]
    public async Task A_rate_limit_is_honoured_until_it_ends()
    {
        _options.GitHubToken = Token;
        _github.Respond(
            HttpStatusCode.Forbidden,
            "{}",
            new Dictionary<string, string>
            {
                ["x-ratelimit-remaining"] = "0",
                ["x-ratelimit-reset"] = _time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var service = Service();

        var first = await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "one", null, null), None);

        first.Reason.Should().Contain("rate limit");
        service.GetStatus().AutoRaise.Should().BeFalse();

        var second = await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "two", null, null), None);

        second.Raised.Should().BeFalse();
        second.Reason.Should().Contain("rate limit");
        _github.Requests.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromMinutes(11));
        _github.Respond(HttpStatusCode.Created, """{"number":3,"html_url":"https://github.com/wulfftech/wondarr/issues/3"}""");

        service.GetStatus().AutoRaise.Should().BeTrue();
        (await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "three", null, null), None)).Raised.Should().BeTrue();
    }

    [Fact]
    public async Task A_retry_after_on_a_429_is_honoured()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.TooManyRequests, "{}", new Dictionary<string, string> { ["Retry-After"] = "120" });

        var service = Service();

        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "one", null, null), None);
        _time.Advance(TimeSpan.FromSeconds(119));
        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "two", null, null), None);

        _github.Requests.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromSeconds(2));
        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "three", null, null), None);

        _github.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, false)]
    [InlineData(HttpRequestError.ConnectionError, false)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    public async Task A_network_failure_falls_back_to_the_form_and_says_when_the_issue_may_exist(HttpRequestError error, bool mayExist)
    {
        _options.GitHubToken = Token;
        _github.Throw(new HttpRequestException(error, "failed"));

        var outcome = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "Broken", null, null), None);

        outcome.Raised.Should().BeFalse();
        outcome.MayBeRaised.Should().Be(mayExist);
        outcome.Reason.Should().Contain("could not be reached");
        _github.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_github_that_never_answers_times_out_on_the_clock_and_falls_back()
    {
        _options.GitHubToken = Token;
        _github.Hang();

        var submit = Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "Broken", null, null), None);

        await _github.Started.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(FeedbackService.RequestTimeout);

        var outcome = await submit.WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Raised.Should().BeFalse();
        outcome.MayBeRaised.Should().BeTrue("the request was sent and GitHub may have acted on it");
        outcome.Reason.Should().Contain("15 seconds");
        _github.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_secondary_rate_limit_403_without_headers_waits_a_minute()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""");

        var service = Service();

        (await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "one", null, null), None)).Reason.Should().Contain("rate limit");
        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "two", null, null), None);

        _github.Requests.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "three", null, null), None);

        _github.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_retry_after_on_a_503_is_honoured()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.ServiceUnavailable, "{}", new Dictionary<string, string> { ["Retry-After"] = "300" });

        var service = Service();

        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "one", null, null), None);
        _time.Advance(TimeSpan.FromMinutes(4));
        await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "two", null, null), None);

        _github.Requests.Should().HaveCount(1);
        service.GetStatus().AutoRaise.Should().BeFalse();
    }

    [Fact]
    public async Task At_most_five_issues_are_opened_in_ten_minutes()
    {
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.Created, """{"number":1,"html_url":"https://github.com/wulfftech/wondarr/issues/1"}""");

        var service = Service();

        for (var index = 0; index < FeedbackService.MaxIssuesPerWindow; index++)
        {
            (await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, $"issue {index}", null, null), None)).Raised.Should().BeTrue();
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        service.GetStatus().AutoRaise.Should().BeFalse();

        var throttled = await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "one too many", null, null), None);

        throttled.Raised.Should().BeFalse();
        throttled.Reason.Should().Contain("at most 5 issues every 10 minutes");
        throttled.IssueUrl.Should().StartWith(FeedbackService.NewIssueUrl);
        _github.Requests.Should().HaveCount(FeedbackService.MaxIssuesPerWindow);

        // The first one leaves the window ten minutes after it was sent.
        _time.Advance(TimeSpan.FromMinutes(5));

        service.GetStatus().AutoRaise.Should().BeTrue();
        (await service.SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, "later", null, null), None)).Raised.Should().BeTrue();
    }

    [Fact]
    public async Task Known_secrets_never_reach_the_public_issue()
    {
        const string ApiKey = "0123456789abcdef0123456789abcdef";
        _secrets.Register(ApiKey);
        _options.GitHubToken = Token;
        _github.Respond(HttpStatusCode.Created, """{"number":1,"html_url":"https://github.com/wulfftech/wondarr/issues/1"}""");

        var text = $"Calling http://wondarr:1077/api/v1/queue?apikey={ApiKey} fails";

        await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, $"/library?key={ApiKey}", ApiKey), None);

        var sent = _github.Requests.Should().ContainSingle().Subject.Body;

        sent.Should().NotContain(ApiKey).And.Contain("(removed)");

        _options.GitHubToken = null;
        var form = await Service().SubmitAsync(new FeedbackSubmission(FeedbackKind.Bug, text, null, null), None);

        Uri.UnescapeDataString(form.IssueUrl).Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task A_heading_with_markdown_in_it_stays_text()
    {
        var outcome = await Service().SubmitAsync(
            new FeedbackSubmission(FeedbackKind.Bug, "x", "/artist/1", "@someone [a](https://evil.example) `x`"),
            None);

        Query(outcome.IssueUrl)["body"].Should().Contain("- **Page:** `/artist/1` (`@someone [a](https://evil.example) x`)");
    }

    [Fact]
    public void The_token_is_trimmed_and_registered_as_a_secret()
    {
        var secrets = new SecretRegistry();
        var options = new FeedbackOptions { GitHubToken = $"  {Token} " };

        new FeedbackOptionsPostConfigure(secrets).PostConfigure(null, options);

        options.GitHubToken.Should().Be(Token);
        secrets.Redact($"token {Token}").Should().Be("token (removed)");
    }

    [Fact]
    public void A_blank_token_counts_as_none()
    {
        var options = new FeedbackOptions { GitHubToken = "   " };

        new FeedbackOptionsPostConfigure(new SecretRegistry()).PostConfigure(null, options);

        options.GitHubToken.Should().BeNull();
    }

    private FeedbackService Service(ILogger<FeedbackService>? logger = null)
    {
        var options = Substitute.For<IOptionsMonitor<FeedbackOptions>>();
        options.CurrentValue.Returns(_ => _options);

        return new FeedbackService(
            new FakeClientFactory(_github),
            options,
            _secrets,
            _time,
            RunningVersion.Parse("0.1.9"),
            logger ?? NullLogger<FeedbackService>.Instance);
    }

    /// <summary>The query parameters of a link, decoded.</summary>
    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<CancellationToken, Task<HttpResponseMessage>> _next =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        // Copied as they arrive: the service disposes the request (and its content) once it has the answer.
        public List<SentRequest> Requests { get; } = [];

        public Task Started => _started.Task;

        public void Respond(HttpStatusCode status, string json, Dictionary<string, string>? headers = null) =>
            _next = _ =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                foreach (var (name, value) in headers ?? [])
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return Task.FromResult(response);
            };

        public void Throw(Exception exception) => _next = _ => throw exception;

        public void Hang() =>
            _next = async token =>
            {
                _started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);

                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            return await _next(cancellationToken);
        }
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingLogger : ILogger<FeedbackService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
