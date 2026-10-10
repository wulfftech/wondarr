using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Core.Tests.Updates;

/// <summary>The update check against a fake GitHub (a handler) and a fake clock.</summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly List<object> _published = [];
    private readonly ServiceProvider _services;
    private readonly UpdateOptions _options = new();

    public UpdateCheckServiceTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task A_stable_build_ignores_pre_releases_and_drafts_and_unparsable_tags()
    {
        _github.Respond(Releases(
            Release("v0.3.0-rc.1", prerelease: true),
            Release("v0.2.0", draft: true),
            Release("nightly"),
            Release("v0.1.5")));

        var status = await Service("0.1.0").CheckAsync(manual: false, Token);

        status.LatestVersion.Should().Be("0.1.5");
        status.UpdateAvailable.Should().BeTrue();
        status.ReleaseUrl.Should().Be("https://github.com/wulfftech/wondarr/releases/tag/v0.1.5");
        status.ReleaseNotes.Should().Be("Notes for v0.1.5");
        status.LastError.Should().BeNull();
        status.CheckedAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task A_release_candidate_hears_about_the_next_candidate_and_the_release()
    {
        _github.Respond(Releases(
            Release("v0.1.0-rc.2", prerelease: true),
            Release("v0.1.0-rc.1", prerelease: true)));

        var service = Service("0.1.0-rc.1");

        (await service.CheckAsync(manual: false, Token)).LatestVersion.Should().Be("0.1.0-rc.2");

        _github.Respond(Releases(
            Release("v0.1.0"),
            Release("v0.1.0-rc.2", prerelease: true)));
        _time.Advance(TimeSpan.FromHours(12));

        var status = await service.CheckAsync(manual: false, Token);

        status.LatestVersion.Should().Be("0.1.0");
        status.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task The_same_version_is_up_to_date()
    {
        _github.Respond(Releases(Release("v0.1.0")));

        var status = await Service("0.1.0").CheckAsync(manual: false, Token);

        status.LatestVersion.Should().Be("0.1.0");
        status.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_development_build_reports_the_latest_release_but_never_an_update()
    {
        _github.Respond(Releases(Release("v0.1.0")));

        var status = await Service("0.0.0-develop.7+abc").CheckAsync(manual: false, Token);

        status.IsDevelopmentBuild.Should().BeTrue();
        status.CurrentVersion.Should().Be("0.0.0-develop.7");
        status.LatestVersion.Should().Be("0.1.0");
        status.UpdateAvailable.Should().BeFalse();
        _published.OfType<UpdateAvailableEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task The_request_goes_to_the_releases_endpoint_without_a_conditional_header_the_first_time()
    {
        _github.Respond(Releases(Release("v0.1.0")));

        await Service("0.1.0").CheckAsync(manual: false, Token);

        _github.Requests.Should().ContainSingle();
        _github.Requests[0].RequestUri!.ToString().Should().Be(UpdateCheckService.ReleasesUrl);
        _github.Requests[0].Headers.Contains("If-None-Match").Should().BeFalse();
    }

    [Fact]
    public async Task The_etag_is_sent_back_and_a_304_reuses_the_list()
    {
        _github.Respond(Releases(Release("v0.1.5")), etag: "\"abc\"");
        var service = Service("0.1.0");

        await service.CheckAsync(manual: false, Token);

        _github.Respond(HttpStatusCode.NotModified);
        _time.Advance(TimeSpan.FromHours(12));

        var status = await service.CheckAsync(manual: false, Token);

        _github.Requests.Should().HaveCount(2);
        _github.Requests[1].Headers.GetValues("If-None-Match").Should().Equal("\"abc\"");
        status.LatestVersion.Should().Be("0.1.5");
        status.UpdateAvailable.Should().BeTrue();
        status.LastError.Should().BeNull();
        status.CheckedAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task A_manual_check_within_a_minute_of_a_good_one_reuses_it()
    {
        _github.Respond(Releases(Release("v0.1.5")));
        var service = Service("0.1.0");

        await service.CheckAsync(manual: false, Token);
        _time.Advance(TimeSpan.FromSeconds(59));
        await service.CheckAsync(manual: true, Token);

        _github.Requests.Should().ContainSingle();

        _time.Advance(TimeSpan.FromSeconds(2));
        await service.CheckAsync(manual: true, Token);

        _github.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_403_with_a_rate_limit_reset_blocks_requests_until_then()
    {
        var reset = _time.GetUtcNow().AddMinutes(30);
        _github.Respond(HttpStatusCode.Forbidden, headers: new()
        {
            ["x-ratelimit-remaining"] = "0",
            ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var service = Service("0.1.0");

        var first = await service.CheckAsync(manual: false, Token);

        first.LastError.Should().StartWith("GitHub's rate limit; next check after ");

        _time.Advance(TimeSpan.FromMinutes(29));
        var blocked = await service.CheckAsync(manual: false, Token);

        _github.Requests.Should().ContainSingle();
        blocked.LastError.Should().StartWith("GitHub's rate limit");

        _github.Respond(Releases(Release("v0.1.5")));
        _time.Advance(TimeSpan.FromMinutes(2));
        var after = await service.CheckAsync(manual: false, Token);

        _github.Requests.Should().HaveCount(2);
        after.LastError.Should().BeNull();
        after.LatestVersion.Should().Be("0.1.5");
    }

    [Fact]
    public async Task A_429_with_retry_after_blocks_requests_for_that_long()
    {
        _github.Respond(HttpStatusCode.TooManyRequests, headers: new() { ["Retry-After"] = "600" });
        var service = Service("0.1.0");

        await service.CheckAsync(manual: false, Token);
        _time.Advance(TimeSpan.FromMinutes(9));
        await service.CheckAsync(manual: false, Token);

        _github.Requests.Should().ContainSingle();

        _github.Respond(Releases(Release("v0.1.5")));
        _time.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAsync(manual: false, Token);

        _github.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_network_error_becomes_the_last_error_and_is_not_thrown()
    {
        _github.Throw(new HttpRequestException("connection refused"));

        var status = await Service("0.1.0").CheckAsync(manual: false, Token);

        status.LastError.Should().StartWith("GitHub could not be reached");
        status.LatestVersion.Should().BeNull();
        status.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_server_error_becomes_the_last_error()
    {
        _github.Respond(HttpStatusCode.InternalServerError);

        var status = await Service("0.1.0").CheckAsync(manual: false, Token);

        status.LastError.Should().Contain("500");
    }

    [Fact]
    public async Task A_failed_check_keeps_the_last_good_answer()
    {
        _github.Respond(Releases(Release("v0.1.5")));
        var service = Service("0.1.0");
        await service.CheckAsync(manual: false, Token);

        _github.Respond(HttpStatusCode.BadGateway);
        _time.Advance(TimeSpan.FromHours(12));
        var status = await service.CheckAsync(manual: false, Token);

        status.LastError.Should().NotBeNull();
        status.LatestVersion.Should().Be("0.1.5");
        status.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_request_that_never_answers_times_out_into_the_last_error()
    {
        _github.Hang();
        var service = Service("0.1.0");

        var check = service.CheckAsync(manual: false, Token);
        await _github.Started;
        _time.Advance(UpdateCheckService.RequestTimeout + TimeSpan.FromSeconds(1));

        var status = await check.WaitAsync(TimeSpan.FromSeconds(30));

        status.LastError.Should().Contain("did not answer");
    }

    [Fact]
    public async Task With_the_check_off_no_request_is_ever_made()
    {
        _options.CheckEnabled = false;
        var service = Service("0.1.0");

        var status = await service.CheckAsync(manual: false, Token);
        await service.CheckAsync(manual: true, Token);

        _github.Requests.Should().BeEmpty();
        status.CheckEnabled.Should().BeFalse();
        status.UpdateAvailable.Should().BeFalse();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_release_notes_are_capped()
    {
        _github.Respond(Releases(Release("v0.1.5", body: new string('x', UpdateCheckService.MaxReleaseNotesLength + 500))));

        var status = await Service("0.1.0").CheckAsync(manual: false, Token);

        status.ReleaseNotes.Should().HaveLength(UpdateCheckService.MaxReleaseNotesLength);
    }

    [Fact]
    public async Task A_new_version_is_announced_once_and_not_again_after_a_restart()
    {
        _github.Respond(Releases(Release("v0.1.5")));
        var service = Service("0.1.0");

        await service.CheckAsync(manual: false, Token);
        _time.Advance(TimeSpan.FromHours(12));
        await service.CheckAsync(manual: false, Token);

        _published.OfType<UpdateAvailableEvent>().Should().ContainSingle()
            .Which.Should().Be(new UpdateAvailableEvent(
                "0.1.0",
                "0.1.5",
                "https://github.com/wulfftech/wondarr/releases/tag/v0.1.5"));

        // A restart: a new service over the same database.
        var restarted = Service("0.1.0");
        _time.Advance(TimeSpan.FromHours(12));
        await restarted.CheckAsync(manual: false, Token);

        _published.OfType<UpdateAvailableEvent>().Should().ContainSingle();

        // A newer version is announced again.
        _github.Respond(Releases(Release("v0.2.0"), Release("v0.1.5")));
        _time.Advance(TimeSpan.FromHours(12));
        await restarted.CheckAsync(manual: false, Token);

        _published.OfType<UpdateAvailableEvent>().Should().HaveCount(2);

        await using var scope = _services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ISettingsRepository>()
            .GetAsync<string>(UpdateCheckService.LastAnnouncedKey, Token)).Should().Be("0.2.0");
    }

    [Fact]
    public async Task Every_check_that_asked_github_publishes_the_status_for_open_tabs()
    {
        _github.Respond(Releases(Release("v0.1.0")));

        await Service("0.1.0").CheckAsync(manual: false, Token);

        _published.OfType<UpdateCheckedEvent>().Should().ContainSingle();
    }

    private UpdateCheckService Service(string version)
    {
        var options = Substitute.For<IOptionsMonitor<UpdateOptions>>();
        options.CurrentValue.Returns(_ => _options);

        return new UpdateCheckService(
            new FakeClientFactory(_github),
            options,
            _services.GetRequiredService<IServiceScopeFactory>(),
            new RecordingEvents(_published),
            _time,
            RunningVersion.Parse(version),
            NullLogger<UpdateCheckService>.Instance);
    }

    private static string Release(string tag, bool prerelease = false, bool draft = false, string? body = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = $"Wondarr {tag}",
            ["html_url"] = $"https://github.com/wulfftech/wondarr/releases/tag/{tag}",
            ["body"] = body ?? $"Notes for {tag}",
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["published_at"] = "2026-10-01T10:00:00Z",
        });

    private static string Releases(params string[] releases) => $"[{string.Join(",", releases)}]";

    private sealed class RecordingEvents(List<object> published) : IEventAggregator
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : IEvent
        {
            published.Add(message);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientFactory(FakeGitHub github) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(github, disposeHandler: false);
    }

    /// <summary>A scripted GitHub: answers the queued response, or throws, and remembers every request.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _next =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        public List<HttpRequestMessage> Requests { get; } = [];

        public Task Started => _started.Task;

        public void Respond(string json, string? etag = null) =>
            _next = (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (etag is not null)
                {
                    response.Headers.TryAddWithoutValidation("ETag", etag);
                }

                return Task.FromResult(response);
            };

        public void Respond(HttpStatusCode status, Dictionary<string, string>? headers = null) =>
            _next = (_, _) =>
            {
                var response = new HttpResponseMessage(status);

                foreach (var (name, value) in headers ?? [])
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return Task.FromResult(response);
            };

        public void Throw(Exception exception) => _next = (_, _) => throw exception;

        public void Hang() =>
            _next = async (_, token) =>
            {
                _started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);

                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return _next(request, cancellationToken);
        }
    }
}
