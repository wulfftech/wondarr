using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists.LastFm;
using Wondarr.Core.Metadata.LastFm;
using Wondarr.Core.Tests.Metadata;
using Wondarr.Core.Tests.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The Last.fm import lists: the loved and top reads against hand-written answers in the shape the
/// 2.0 API documents, the paging, the error mapping, and the rule that the API key is sent but never
/// comes back in a string Wondarr shows or stores.
/// </summary>
public sealed class LastFmProvidersTests
{
    /// <summary>A key that is easy to spot in a message that should not carry it.</summary>
    private const string ApiKey = "k3y-0000-SECRET";

    private const string User = "someone";

    /// <summary>The base address the named <c>lastfm</c> client is registered with.</summary>
    private static readonly string BaseAddress = Wondarr.Core.Metadata.ServiceCollectionExtensions.LastFmBaseUrl;

    [Fact]
    public async Task Loved_reads_one_page_and_maps_its_tracks()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(LovedPage(Track("Get Lucky", "Daft Punk"), 1)));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().ContainSingle();
        result.Entries[0].ExternalId.Should().Be("lastfm:daft punk|get lucky");
        result.Entries[0].Artist.Should().Be("Daft Punk");
        result.Entries[0].Title.Should().Be("Get Lucky");

        // Last.fm writes an empty string where it does not know the recording.
        result.Entries[0].MbRecordingId.Should().BeNull();

        var asked = handler.Requests.Should().ContainSingle().Subject;
        asked.Query.Should().Contain("method=user.getlovedtracks");
        asked.Query.Should().Contain($"user={User}");
        asked.Query.Should().Contain("format=json");
        asked.Query.Should().Contain("limit=200");
        asked.Query.Should().Contain("page=1");
    }

    [Fact]
    public async Task Loved_pages_until_the_last_page()
    {
        var handler = StubHttpMessageHandler.Scripted(request => Json(LovedPage(
            Track(request.Query.Contains("page=1", StringComparison.Ordinal) ? "One" : "Two", "Artist"),
            2)));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Select(entry => entry.Title).Should().Equal(["One", "Two"]);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Query.Should().Contain("page=2");
    }

    [Fact]
    public async Task Loved_fails_when_the_user_is_unknown()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":6,"message":"User not found"}"""));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Entries.Should().BeEmpty();
        result.Error.Should().Be("Last.fm: User not found");
    }

    [Fact]
    public async Task Loved_fails_on_a_rate_limit_with_a_try_later_message()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":29,"message":"Rate limit exceeded"}"""));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        result.Error.Should().Be("Last.fm: Rate limit exceeded; the list will be read again later.");
    }

    [Fact]
    public async Task Loved_fails_when_the_answer_is_a_web_page()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Html("<html><body>Verifying your browser</body></html>"));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Entries.Should().BeEmpty();
        result.Error.Should().Contain("not JSON");
        result.Error.Should().Contain("read again later");
    }

    [Fact]
    public async Task Top_reads_the_period_the_count_and_the_durations()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TopPage(
            Track("Get Lucky", "Daft Punk", "369") + "," + Track("Something Else", "Other"),
            1)));
        var provider = new LastFmTopProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(
            List(LastFmTopProvider.LastFmTopType, """{"count":2,"period":"overall"}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(2);
        result.Entries[0].DurationMs.Should().Be(369_000);
        result.Entries[1].DurationMs.Should().BeNull();

        var asked = handler.Requests.Should().ContainSingle().Subject;
        asked.Query.Should().Contain("method=user.gettoptracks");
        asked.Query.Should().Contain("period=overall");
        asked.Query.Should().Contain("limit=2");
    }

    [Fact]
    public async Task Top_defaults_to_the_last_year_and_fifty_tracks()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TopPage(Track("Get Lucky", "Daft Punk", "369"), 1)));
        var provider = new LastFmTopProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmTopProvider.LastFmTopType), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Query.Should().Contain("period=12month");
        handler.Requests[0].Query.Should().Contain("limit=50");
    }

    [Fact]
    public async Task Top_pages_until_the_count_is_reached()
    {
        var handler = StubHttpMessageHandler.Scripted(request => Json(TopPage(
            request.Query.Contains("page=1", StringComparison.Ordinal)
                ? string.Join(",", Enumerable.Range(1, 4).Select(index => Track($"Song {index}", "Artist")))
                : string.Join(",", Enumerable.Range(5, 2).Select(index => Track($"Song {index}", "Artist"))),
            2)));
        var provider = new LastFmTopProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(
            List(LastFmTopProvider.LastFmTopType, """{"count":4}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Select(entry => entry.Title).Should().Equal(["Song 1", "Song 2", "Song 3", "Song 4"]);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Top_fails_without_the_key_in_the_message()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":26,"message":"Invalid API key"}"""));
        var provider = new LastFmTopProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmTopProvider.LastFmTopType), CancellationToken.None);

        result.Error.Should().Be("Last.fm: Invalid API key");
        result.Error.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task The_key_is_sent_but_never_comes_back_in_a_returned_string()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":10,"message":"Invalid API key"}"""));
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(List(LastFmLovedProvider.LastFmLovedType), CancellationToken.None);

        // The key travels in the query, as Last.fm's own clients send it …
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Query.Should().Contain("api_key=" + ApiKey);

        // … and never in anything the provider hands back: the provider logs nothing at all.
        result.Error.Should().NotContain(ApiKey);
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Validate_asks_for_the_user_and_the_key()
    {
        var provider = new LastFmLovedProvider(new ListHttpClientFactory(Empty(), BaseAddress), TimeProvider.System);

        // Empty settings are missing both fields, and the form shows both messages at once.
        var problems = provider.Validate(Settings("{}"), null);
        problems.Should().HaveCount(2);
        problems.Should().Contain(problem => problem.Contains("user", StringComparison.Ordinal));
        problems.Should().Contain(problem => problem.Contains("API key", StringComparison.Ordinal));

        provider.Validate(Settings("""{"user":"someone"}"""), null).Should().ContainSingle().Which.Should().Contain("API key");
        provider.Validate(Settings($$"""{"user":"{{User}}","apiKey":"{{ApiKey}}"}"""), null).Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_without_a_key_of_its_own_reads_with_the_global_one()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(LovedPage(Track("Get Lucky", "Daft Punk"), 1)));
        var provider = new LastFmLovedProvider(
            new ListHttpClientFactory(handler, BaseAddress),
            TimeProvider.System,
            Global("global-key-0001"));

        var result = await provider.FetchAsync(
            new ImportList { Type = LastFmLovedProvider.LastFmLovedType, Settings = $$"""{"user":"{{User}}"}""" },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle().Which.Query.Should().Contain("api_key=global-key-0001");
    }

    [Fact]
    public async Task A_lists_own_key_wins_over_the_global_one()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TopPage(Track("Get Lucky", "Daft Punk"), 1)));
        var provider = new LastFmTopProvider(
            new ListHttpClientFactory(handler, BaseAddress),
            TimeProvider.System,
            Global("global-key-0001"));

        await provider.FetchAsync(List(LastFmTopProvider.LastFmTopType), CancellationToken.None);

        var query = handler.Requests.Should().ContainSingle().Subject.Query;
        query.Should().Contain($"api_key={ApiKey}").And.NotContain("global-key-0001");
    }

    [Fact]
    public async Task A_list_with_no_key_anywhere_still_reports_that_it_needs_one()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("{}"));
        var provider = new LastFmLovedProvider(
            new ListHttpClientFactory(handler, BaseAddress),
            TimeProvider.System,
            Global(null));

        var result = await provider.FetchAsync(
            new ImportList { Type = LastFmLovedProvider.LastFmLovedType, Settings = $$"""{"user":"{{User}}"}""" },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("API key");
        handler.Requests.Should().BeEmpty();

        provider.Validate(Settings($$"""{"user":"{{User}}"}"""), null).Should().ContainSingle().Which.Should().Contain("API key");
    }

    [Fact]
    public void With_a_global_key_the_form_no_longer_asks_for_one_per_list()
    {
        var provider = new LastFmLovedProvider(
            new ListHttpClientFactory(Empty(), BaseAddress),
            TimeProvider.System,
            Global("global-key-0001"));

        provider.Validate(Settings($$"""{"user":"{{User}}"}"""), null).Should().BeEmpty();
        provider.Fields.Single(field => field.Name == "apiKey").HelpText.Should().Contain("Settings");
    }

    [Fact]
    public void Top_rejects_an_unknown_period_and_an_out_of_range_count()
    {
        var provider = new LastFmTopProvider(new ListHttpClientFactory(Empty(), BaseAddress), TimeProvider.System);

        provider.Validate(Settings("""{"user":"s","apiKey":"k","period":"fortnight"}"""), null)
            .Should().ContainSingle().Which.Should().Contain("period");
        provider.Validate(Settings("""{"user":"s","apiKey":"k","count":0}"""), null)
            .Should().ContainSingle().Which.Should().Contain("between 1 and 500");
        provider.Validate(Settings("""{"user":"s","apiKey":"k","count":501}"""), null)
            .Should().ContainSingle().Which.Should().Contain("between 1 and 500");
        provider.Validate(Settings("""{"user":"s","apiKey":"k","period":"7day","count":100}"""), null).Should().BeEmpty();
    }

    [Fact]
    public void The_forms_name_the_key_a_secret()
    {
        new LastFmLovedProvider(new ListHttpClientFactory(Empty(), BaseAddress), TimeProvider.System).Fields
            .Single(field => field.Name == "apiKey").Secret.Should().BeTrue();
        new LastFmTopProvider(new ListHttpClientFactory(Empty(), BaseAddress), TimeProvider.System).Fields
            .Single(field => field.Name == "apiKey").Secret.Should().BeTrue();
    }

    private static StaticOptionsMonitor<LastFmOptions> Global(string? apiKey) =>
        new(new LastFmOptions { ApiKey = apiKey });

    private static ImportList List(string type, string? settings = null)
    {
        // The user and the key every Last.fm read needs, with whatever the test overrides on top.
        var merged = new JsonObject
        {
            ["user"] = User,
            ["apiKey"] = ApiKey,
        };

        if (settings is not null)
        {
            foreach (var property in JsonNode.Parse(settings)!.AsObject())
            {
                merged[property.Key] = property.Value?.DeepClone();
            }
        }

        return new ImportList { Type = type, Settings = merged.ToJsonString() };
    }

    private static string LovedPage(string track, int totalPages) => string.Concat(
        @"{""lovedtracks"":{""track"":[",
        track,
        @"],""@attr"":{""page"":""1"",""totalPages"":""",
        totalPages.ToString(CultureInfo.InvariantCulture),
        @""",""perPage"":""200"",""total"":""1"",""user"":""x""}}}");

    private static string TopPage(string tracks, int totalPages) => string.Concat(
        @"{""toptracks"":{""track"":[",
        tracks,
        @"],""@attr"":{""page"":""1"",""totalPages"":""",
        totalPages.ToString(CultureInfo.InvariantCulture),
        @""",""perPage"":""200"",""total"":""1"",""user"":""x""}}}");

    private static string Track(string name, string artist, string? duration = null) => string.Concat(
        @"{""name"":""",
        name,
        @""",""mbid"":"""",""duration"":""",
        duration ?? "0",
        @""",""artist"":{""name"":""",
        artist,
        @""",""mbid"":""""}}");

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static StubHttpMessageHandler Empty() => StubHttpMessageHandler.Scripted(_ => Json("{}"));

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}

/// <summary>
/// Hands out clients built on one stub handler and the service's base address, standing in for
/// <c>IHttpClientFactory</c>: the providers ask for a relative URL, which only resolves against a
/// base address. Shared by both list test files in this folder.
/// </summary>
internal sealed class ListHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly string _baseAddress;

    public ListHttpClientFactory(HttpMessageHandler handler, string baseAddress)
    {
        _handler = handler;
        _baseAddress = baseAddress;
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(_baseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(15),
        };
}
