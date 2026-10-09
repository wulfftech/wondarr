using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.LastFm;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Metadata services card's backend: which optional keys are set, the partial update, the
/// environment lock and the Test buttons. Last.fm and AcoustID are stubbed at the HTTP layer, so the
/// real clients run and no test reaches the network.
/// </summary>
/// <remarks>
/// Not parallelised: the environment-lock test sets a real <c>APP__LASTFM__API_KEY</c> variable for
/// the duration of its run, and a concurrently starting host would read it.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetadataSettingsApiTestsSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "Metadata settings API";
}

/// <inheritdoc cref="MetadataSettingsApiTestsSeries" />
[Collection(MetadataSettingsApiTestsSeries.Name)]
public sealed class MetadataSettingsApiTests
{
    private const string Endpoint = "/api/v1/metadata/settings";

    /// <summary>A key that is easy to spot in a response that should not carry it.</summary>
    private const string LastFmKey = "lastfm-SECRET-0123456789abcdef";

    private const string AcoustIdKey = "acoustid-SECRET-0123456789";

    [Fact]
    public async Task Nothing_is_set_until_a_key_is_written()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, body) = await api.GetAsync(Endpoint);

        status.Should().Be(HttpStatusCode.OK);
        Flags(body).Should().Equal(false, false, false, false);
    }

    [Fact]
    public async Task A_key_is_stored_in_the_file_applied_live_and_never_returned()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, put) = await api.PutAsync(Endpoint, new { acoustIdClientKey = AcoustIdKey, lastFmApiKey = LastFmKey });

        status.Should().Be(HttpStatusCode.OK);

        var (_, get) = await api.GetAsync(Endpoint);

        Flags(get).Should().Equal(true, false, true, false);
        api.Raw.Should().NotContain(LastFmKey).And.NotContain(AcoustIdKey);
        put.Should().NotBeNull();

        // The file carries the keys, so a restart does not lose them.
        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("lastfm:").And.Contain($"api_key: \"{LastFmKey}\"");
        yaml.Should().Contain("acoustid:").And.Contain($"client_key: \"{AcoustIdKey}\"");

        // And the options reload, so the song page sees the key without a restart.
        factory.Services.GetRequiredService<ILastFmClient>().IsConfigured.Should().BeTrue();
        factory.Services.GetRequiredService<IOptionsMonitor<AcoustIdOptions>>().CurrentValue.ClientKey.Should().Be(AcoustIdKey);
    }

    [Fact]
    public async Task A_field_the_body_leaves_out_is_unchanged_and_an_empty_string_removes_the_key()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        await api.PutAsync(Endpoint, new { acoustIdClientKey = AcoustIdKey, lastFmApiKey = LastFmKey });

        // Only Last.fm is mentioned: the AcoustID key stays.
        var (_, unchanged) = await api.PutAsync(Endpoint, new { lastFmApiKey = LastFmKey + "2" });

        Flags(unchanged).Should().Equal(true, false, true, false);

        var (_, removed) = await api.PutAsync(Endpoint, new { lastFmApiKey = string.Empty });

        Flags(removed).Should().Equal(true, false, false, false);

        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().NotContain(LastFmKey).And.NotContain("api_key: \"");
        yaml.Should().Contain("client_key");
        factory.Services.GetRequiredService<ILastFmClient>().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task A_key_the_environment_sets_is_locked_and_a_change_is_refused_with_the_variable_named()
    {
        // The service reads the process environment, so the variable is set for real and cleared again.
        Environment.SetEnvironmentVariable("APP__LASTFM__API_KEY", LastFmKey);

        try
        {
            using var factory = new WondarrAppFactory();
            using var api = new Session(factory);

            var (_, read) = await api.GetAsync(Endpoint);

            Flags(read).Should().Equal(false, false, true, true);
            api.Raw.Should().NotContain(LastFmKey);

            var (status, body) = await api.PutAsync(Endpoint, new { lastFmApiKey = "something-else-123456" });

            status.Should().Be(HttpStatusCode.BadRequest);

            var messages = ((JsonObject)body!)["errors"]!["settings"]!.AsArray();

            messages.Should().Contain(message => message!.GetValue<string>().Contains("APP__LASTFM__API_KEY"));
            api.Raw.Should().NotContain(LastFmKey);

            // A field the environment does not own still changes in the same request's absence.
            var (okStatus, _) = await api.PutAsync(Endpoint, new { acoustIdClientKey = AcoustIdKey });

            okStatus.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__LASTFM__API_KEY", null);
        }
    }

    [Fact]
    public async Task Testing_last_fm_with_a_typed_key_makes_one_track_get_info_call_and_reports_ok()
    {
        var last = new StubHandler(_ => Json(LastFmTrack));
        using var factory = Factory(lastFm: last);
        using var api = new Session(factory);

        var (status, body) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm", key = LastFmKey });

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["ok"]!.GetValue<bool>().Should().BeTrue();
        api.Raw.Should().NotContain(LastFmKey);

        var asked = last.Requests.Should().ContainSingle().Subject;
        asked.Query.Should().Contain("method=track.getInfo").And.Contain($"api_key={LastFmKey}");
    }

    [Fact]
    public async Task Testing_last_fm_without_a_typed_key_uses_the_stored_one_and_without_either_says_so()
    {
        var last = new StubHandler(_ => Json(LastFmTrack));
        using var factory = Factory(lastFm: last);
        using var api = new Session(factory);

        var (_, none) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm" });

        ((JsonObject)none!)["ok"]!.GetValue<bool>().Should().BeFalse();
        last.Requests.Should().BeEmpty("there is no key to try");

        await api.PutAsync(Endpoint, new { lastFmApiKey = LastFmKey });
        var (_, stored) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm" });

        ((JsonObject)stored!)["ok"]!.GetValue<bool>().Should().BeTrue();
        last.Requests.Should().ContainSingle().Which.Query.Should().Contain($"api_key={LastFmKey}");
    }

    [Fact]
    public async Task Testing_last_fm_reports_an_invalid_key_and_a_rate_limit()
    {
        var invalid = new StubHandler(_ => Json("""{"error":10,"message":"Invalid API key - You must be granted a valid key by last.fm"}"""));
        using (var factory = Factory(lastFm: invalid))
        using (var api = new Session(factory))
        {
            var (status, body) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm", key = LastFmKey });

            status.Should().Be(HttpStatusCode.OK);

            var result = (JsonObject)body!;

            result["ok"]!.GetValue<bool>().Should().BeFalse();
            result["message"]!.GetValue<string>().Should().Contain("rejected");
            api.Raw.Should().NotContain(LastFmKey);
        }

        var limited = new StubHandler(_ => Json("""{"error":29,"message":"Rate limit exceeded"}"""));
        using (var factory = Factory(lastFm: limited))
        using (var api = new Session(factory))
        {
            var (_, body) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm", key = LastFmKey });

            var result = (JsonObject)body!;

            result["ok"]!.GetValue<bool>().Should().BeFalse();
            result["message"]!.GetValue<string>().Should().Contain("rate limiting");
        }
    }

    [Fact]
    public async Task Testing_acoustid_treats_a_rejected_probe_fingerprint_as_an_accepted_key()
    {
        var accepted = new StubHandler(_ => Json("""{"status":"error","error":{"code":3,"message":"invalid fingerprint"}}"""));
        using (var factory = Factory(acoustId: accepted))
        using (var api = new Session(factory))
        {
            var (_, body) = await api.PostAsync($"{Endpoint}/test", new { service = "acoustid", key = AcoustIdKey });

            var result = (JsonObject)body!;

            result["ok"]!.GetValue<bool>().Should().BeTrue();
            result["message"]!.GetValue<string>().Should().Contain("refused the probe fingerprint").And.Contain("invalid fingerprint");
            api.Raw.Should().NotContain(AcoustIdKey);
            accepted.Requests.Should().ContainSingle("one lookup, never more");
        }

        var rejected = new StubHandler(_ => Json("""{"status":"error","error":{"code":4,"message":"invalid API key"}}"""));
        using (var factory = Factory(acoustId: rejected))
        using (var api = new Session(factory))
        {
            var (_, body) = await api.PostAsync($"{Endpoint}/test", new { service = "acoustid", key = AcoustIdKey });

            var result = (JsonObject)body!;

            result["ok"]!.GetValue<bool>().Should().BeFalse();
            result["message"]!.GetValue<string>().Should().Contain("rejected");
        }
    }

    [Fact]
    public async Task Testing_acoustid_accepts_the_key_on_any_error_that_is_not_the_invalid_key_one()
    {
        var other = new StubHandler(_ => Json("""{"status":"error","error":{"code":2,"message":"missing required parameter"}}"""));
        using var factory = Factory(acoustId: other);
        using var api = new Session(factory);

        var (_, body) = await api.PostAsync($"{Endpoint}/test", new { service = "acoustid", key = AcoustIdKey });

        var result = (JsonObject)body!;

        result["ok"]!.GetValue<bool>().Should().BeTrue();
        result["message"]!.GetValue<string>().Should().Contain("missing required parameter");
    }

    [Fact]
    public async Task A_429_from_last_fm_is_not_retried_by_the_pipeline_and_holds_the_next_call_back()
    {
        // If the lastfm pipeline retried a 429 (honouring Retry-After) this would see several requests,
        // or sit out the Retry-After; the client reads Retry-After itself and records the wait instead.
        var limited = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(3));

            return response;
        });
        using var factory = Factory(lastFm: limited);
        using var api = new Session(factory);

        var (_, first) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm", key = LastFmKey });
        var (_, second) = await api.PostAsync($"{Endpoint}/test", new { service = "lastfm", key = LastFmKey });

        ((JsonObject)first!)["ok"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)second!)["message"]!.GetValue<string>().Should().Contain("3 minutes");
        limited.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_service_is_refused()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, _) = await api.PostAsync($"{Endpoint}/test", new { service = "spotify", key = "x" });

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    private static WondarrAppFactory Factory(StubHandler? lastFm = null, StubHandler? acoustId = null) =>
        new(configureServices: services =>
        {
            if (lastFm is not null)
            {
                services.AddHttpClient("lastfm").ConfigurePrimaryHttpMessageHandler(() => lastFm);
            }

            if (acoustId is not null)
            {
                services.AddHttpClient<IAcoustIdClient, AcoustIdClient>().ConfigurePrimaryHttpMessageHandler(() => acoustId);
            }
        });

    /// <summary>The four flags in the order the resource declares them.</summary>
    private static bool[] Flags(JsonNode? body)
    {
        var settings = (JsonObject)body!;

        return
        [
            settings["acoustIdKeySet"]!.GetValue<bool>(),
            settings["acoustIdLocked"]!.GetValue<bool>(),
            settings["lastFmKeySet"]!.GetValue<bool>(),
            settings["lastFmLocked"]!.GetValue<bool>(),
        ];
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string LastFmTrack = """{"track":{"name":"Believe","url":"https://www.last.fm/music/Cher/_/Believe","listeners":"1","playcount":"2"}}""";

    /// <summary>Answers every request from a script and keeps what was asked.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;
        private readonly object _sync = new();
        private readonly List<Uri> _requests = [];

        public StubHandler(Func<Uri, HttpResponseMessage> responder) => _responder = responder;

        /// <summary>The URIs asked for, in order.</summary>
        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_sync)
                {
                    return [.. _requests];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _requests.Add(request.RequestUri!);
            }

            return Task.FromResult(_responder(request.RequestUri!));
        }
    }

    /// <summary>The API's client for one test, keeping every response body it saw.</summary>
    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;
        private readonly StringBuilder _raw = new();

        public Session(WondarrAppFactory factory)
        {
            _client = factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        }

        /// <summary>Every response body so far, concatenated.</summary>
        public string Raw => _raw.ToString();

        public Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path, object body) =>
            SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body));

        public Task<(HttpStatusCode Status, JsonNode? Body)> PutAsync(string path, object body) =>
            SendAsync(HttpMethod.Put, path, JsonSerializer.Serialize(body));

        public void Dispose() => _client.Dispose();

        private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, string? json)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            _raw.Append(raw);

            if (raw.Length > 0 && raw[0] != '{')
            {
                throw new InvalidOperationException($"{(int)response.StatusCode} answered a body that is not JSON: {raw[..Math.Min(raw.Length, 600)]}");
            }

            return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
        }
    }
}
