using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Metadata.ListenBrainz;
using Wondarr.Core.Tests.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.Discover;

/// <summary>The ListenBrainz Labs client against canned answers, with its own gate on a fake clock. No network.</summary>
public sealed class ListenBrainzLabsClientTests
{
    private const string SeedMbid = "e97f805a-ab48-4c52-855e-07049142113d";
    private const string Algorithm = "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30_top_n_listeners_1000";

    /// <summary>Cut down from the live answer of 2026-11-17.</summary>
    private const string SampleBody =
        """[{"recording_mbid":"f3bba4cd-8018-468b-902e-bc8f029593e5","recording_name":"Teardrop","artist_credit_name":"Massive Attack","artist_credit_mbids":null,"release_name":"Mezzanine","release_mbid":"2fa63133-a4c9-3f41-8deb-162189de83ff","caa_id":12203062606,"caa_release_mbid":"6e512ec0-1316-4abb-b6b6-07dbd8b005ed","score":230,"reference_mbid":"e97f805a-ab48-4c52-855e-07049142113d"}]""";

    [Fact]
    public async Task It_asks_for_similar_recordings_of_the_seed_and_reads_the_array()
    {
        var handler = Respond(HttpStatusCode.OK, SampleBody);

        var result = await Client(handler).GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None);

        result.Status.Should().Be(ListenBrainzLabsStatus.Ok);

        var recording = result.Recordings.Should().ContainSingle().Subject;
        recording.RecordingMbid.Should().Be("f3bba4cd-8018-468b-902e-bc8f029593e5");
        recording.RecordingName.Should().Be("Teardrop");
        recording.ArtistCreditName.Should().Be("Massive Attack");
        recording.ReleaseName.Should().Be("Mezzanine");
        recording.CaaReleaseMbid.Should().Be("6e512ec0-1316-4abb-b6b6-07dbd8b005ed");
        recording.Score.Should().Be(230);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.AbsolutePath.Should().Be("/similar-recordings/json");
        request.Query.Should().Be($"?recording_mbids={SeedMbid}&algorithm={Algorithm}");
    }

    [Fact]
    public async Task An_empty_array_is_an_answer_with_nothing_in_it()
    {
        var result = await Client(Respond(HttpStatusCode.OK, "[]"))
            .GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None);

        result.Status.Should().Be(ListenBrainzLabsStatus.Ok);
        result.Recordings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_400_that_names_the_enumeration_means_the_algorithm_is_rejected()
    {
        var result = await Client(Respond(
                HttpStatusCode.BadRequest,
                """{"code":400,"error":"algorithm: value is not a valid enumeration member; permitted: x, y"}"""))
            .GetSimilarRecordingsAsync(SeedMbid, "gone", CancellationToken.None);

        result.Status.Should().Be(ListenBrainzLabsStatus.AlgorithmRejected);
        result.Message.Should().Contain("permitted: x, y");
        result.Recordings.Should().BeEmpty();
    }

    [Fact]
    public async Task Any_other_failure_is_unavailable_and_never_throws()
    {
        (await Client(Respond(HttpStatusCode.BadRequest, """{"error":"bad mbid"}"""))
            .GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None)).Status
            .Should().Be(ListenBrainzLabsStatus.Unavailable);

        (await Client(Respond(HttpStatusCode.BadGateway, "<html>"))
            .GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None)).Status
            .Should().Be(ListenBrainzLabsStatus.Unavailable);

        (await Client(Respond(HttpStatusCode.OK, "<html>not json</html>"))
            .GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None)).Status
            .Should().Be(ListenBrainzLabsStatus.Unavailable);
    }

    [Fact]
    public async Task A_429_with_retry_after_is_rate_limited_and_holds_the_gate_for_everyone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        var handler = StubHttpMessageHandler.Scripted(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

            return response;
        });

        var client = Client(handler, gate);

        var result = await client.GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None);

        result.Status.Should().Be(ListenBrainzLabsStatus.RateLimited);
        gate.Cooldown.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Calls_are_spaced_one_second_apart()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);
        var handler = Respond(HttpStatusCode.OK, "[]");
        var client = Client(handler, gate);

        await client.GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None);

        var second = client.GetSimilarRecordingsAsync(SeedMbid, Algorithm, CancellationToken.None);
        await Task.Yield();

        second.IsCompleted.Should().BeFalse("the gate holds the second call for the rest of the second");
        handler.Requests.Should().HaveCount(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await second;

        handler.Requests.Should().HaveCount(2);
    }

    private static StubHttpMessageHandler Respond(HttpStatusCode status, string body) =>
        StubHttpMessageHandler.Scripted(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static ListenBrainzLabsClient Client(StubHttpMessageHandler handler, RequestSpacingGate? gate = null)
    {
        HttpMessageHandler pipeline = gate is null
            ? handler
            : new RequestSpacingHandler(gate, ListenBrainzLabsClient.Provider) { InnerHandler = handler };

        return new ListenBrainzLabsClient(new HttpClient(pipeline) { BaseAddress = new Uri("https://labs.api.listenbrainz.org/") });
    }
}
