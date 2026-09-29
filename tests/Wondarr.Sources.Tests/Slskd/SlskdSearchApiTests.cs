using System.Net;
using System.Text.Json;
using Wondarr.Core.Logging;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The search endpoints against the responses recorded from a live slskd 0.26.0 on 2026-09-29.
/// Every request the client makes carries the API key and nothing else identifies it.
/// </summary>
public class SlskdSearchApiTests
{
    private static readonly Guid SearchId = Guid.Parse("04b0dba5-672a-4256-9f11-88343e389a93");

    [Fact]
    public async Task Posts_a_search_with_the_configured_parameters()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("live/search-create.json"));

        var search = await Api(handler).StartAsync(Request(), CancellationToken.None);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be("http://127.0.0.1:5030/api/v0/searches");
        sent.Headers.GetValues(SlskdClient.ApiKeyHeader)
            .Should().ContainSingle().Which.Should().Be(SlskdTestData.Secrets.ApiKey);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        var json = body.RootElement;
        json.GetProperty("id").GetGuid().Should().Be(SearchId);
        json.GetProperty("searchText").GetString().Should().Be("daft punk get lucky");
        json.GetProperty("searchTimeout").GetInt32().Should().Be(8000);
        json.GetProperty("responseLimit").GetInt32().Should().Be(100);
        json.GetProperty("fileLimit").GetInt32().Should().Be(2000);
        json.GetProperty("minimumPeerUploadSpeed").GetInt32().Should().Be(1);

        search.Id.Should().Be(SearchId);
        search.SearchText.Should().Be("daft punk get lucky");
        search.State.Should().Be("InProgress");
        search.IsComplete.Should().BeFalse();
        search.StartedAt.Should().Be(new DateTime(2026, 9, 28, 23, 7, 8, DateTimeKind.Utc).AddTicks(3494141));
        search.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reads_a_completed_search_with_its_own_counts_and_state()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("live/search-complete.json"));

        var search = await Api(handler).GetAsync(SearchId, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().Should().Be($"http://127.0.0.1:5030/api/v0/searches/{SearchId:D}");

        search.Should().NotBeNull();
        search!.IsComplete.Should().BeTrue();
        search.State.Should().Be("Completed, ResponseLimitReached");
        search.ResponseCount.Should().Be(101);
        search.FileCount.Should().Be(288);
        search.LockedFileCount.Should().Be(31);
        search.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reads_the_recorded_responses_into_their_audio_attributes()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("live/search-responses.json"));

        var responses = await Api(handler).GetResponsesAsync(SearchId, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be($"http://127.0.0.1:5030/api/v0/searches/{SearchId:D}/responses");

        responses.Should().HaveCount(100);
        responses.Sum(response => response.Files.Count).Should().Be(286);
        responses.Sum(response => response.LockedFiles.Count).Should().Be(31);

        var first = responses[0].Files[0];
        first.Length.Should().Be(249);
        first.BitDepth.Should().Be(16);
        first.SampleRate.Should().Be(44100);
        first.BitRate.Should().BeNull();
        first.Extension.Should().BeEmpty();
        first.Filename.Should().Contain("\\");
        first.IsLocked.Should().BeFalse();

        responses[0].Username.Should().NotBeNullOrWhiteSpace();
        responses[0].UploadSpeed.Should().BePositive();
    }

    [Fact]
    public async Task Answers_an_empty_list_for_a_search_slskd_no_longer_has()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var api = Api(handler);

        var search = await api.GetAsync(SearchId, CancellationToken.None);
        var responses = await api.GetResponsesAsync(SearchId, CancellationToken.None);

        search.Should().BeNull();
        responses.Should().BeEmpty();

        // Stopping or deleting a search that is already gone is the same outcome as doing it again.
        await api.StopAsync(SearchId, CancellationToken.None);
        await api.DeleteAsync(SearchId, CancellationToken.None);

        handler.Requests.Select(request => request.Method)
            .Should().Equal(HttpMethod.Get, HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete);
    }

    [Fact]
    public async Task Throws_a_search_rejected_exception_on_a_429()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.TooManyRequests);

        var act = () => Api(handler).StartAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<SlskdSearchRejectedException>())
            .Which.Message.Should().Be("slskd refused the search: too many searches in flight");
    }

    [Fact]
    public async Task Surfaces_any_other_failure_as_an_http_request_exception_without_the_body()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "api-key 'abc' was rejected");

        var act = () => Api(handler).GetAsync(SearchId, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().NotContain("api-key");
    }

    [Fact]
    public async Task Deletes_a_search_with_a_204()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.NoContent);

        await Api(handler).DeleteAsync(SearchId, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Delete);
    }

    private static SlskdSearchRequest Request() =>
        new(SearchId, "daft punk get lucky", 8000, 100, 2000, 1);

    private static SlskdSearchApi Api(StubHttpMessageHandler handler)
    {
        var secrets = new SlskdSecretsStore(
            SlskdTestData.RepositoryWithRuntimeSecrets(),
            Substitute.For<ISecretRegistry>(),
            SlskdTestData.Monitor(new SoulseekOptions()));

        return new SlskdSearchApi(new HttpClient(handler), SlskdTestData.Monitor(new SoulseekOptions()), secrets);
    }
}
