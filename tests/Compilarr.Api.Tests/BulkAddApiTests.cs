using System.Net;
using System.Text;
using System.Text.Json;
using Compilarr.Core.Identity;
using Compilarr.Core.Organizer;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// The bulk add over HTTP: a pasted block becomes a list and a command, the command resolves it with
/// faked providers, and the review screen reads it back. Nothing here reaches the network.
/// </summary>
public sealed class BulkAddApiTests
{
    private const string BulkEndpoint = "/api/v1/song/bulk";
    private const string ListEndpoint = "/api/v1/importlist";
    private const string ItemEndpoint = "/api/v1/importlistitem";
    private const string CommandEndpoint = "/api/v1/command";

    /// <summary>The longest a test waits for a command to reach a state.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string QueenLine = "Queen - Bohemian Rhapsody";
    private const string NobodyLine = "Nobody - Nothing";
    private const string MysteryLine = "Mystery - Song";
    private const string AnotherLine = "Another - Mystery";

    private const string QueenRecording = "11111111-1111-1111-1111-111111111111";
    private const string NobodyRecording = "22222222-2222-2222-2222-222222222222";
    private const string MysteryRecording = "33333333-3333-3333-3333-333333333333";

    /// <summary>An id no fake knows, so the resolve runs into <c>SongNotFoundException</c>.</summary>
    private const string UnknownRecording = "99999999-9999-9999-9999-999999999999";

    [Fact]
    public async Task Bulk_add_stores_the_list_processes_it_and_leaves_the_unresolved_lines_for_review()
    {
        using var factory = SongApiTests.FakeProviders(Resolver());
        using var client = SongApiTests.Authenticated(factory);

        using var posted = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new
            {
                text = $"{QueenLine}\n{NobodyLine}\n{MysteryLine}\n{AnotherLine}",
            })));

        // 202 straight away: the resolve runs as a command the caller can watch.
        posted.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var accepted = await SongApiTests.ReadJsonAsync(posted);
        var listId = accepted.GetProperty("importListId").GetInt64();
        var commandId = accepted.GetProperty("commandId").GetInt64();

        accepted.GetProperty("lineCount").GetInt32().Should().Be(4);

        var finished = await WaitForTerminalStatusAsync(client, commandId);

        finished.GetProperty("status").GetString().Should().Be("completed");
        finished.GetProperty("message").GetString()
            .Should().Be("4 lines: 2 added, 0 already in the library, 2 unresolved, 0 skipped");

        using var header = await client.GetAsync(new Uri($"{ListEndpoint}/{listId}", UriKind.Relative));
        var list = await SongApiTests.ReadJsonAsync(header);

        header.StatusCode.Should().Be(HttpStatusCode.OK);
        list.GetProperty("type").GetString().Should().Be("paste");
        list.GetProperty("lastSyncedAt").ValueKind.Should().Be(JsonValueKind.String);
        list.GetProperty("counts").GetProperty("pending").GetInt32().Should().Be(0);
        list.GetProperty("counts").GetProperty("added").GetInt32().Should().Be(2);
        list.GetProperty("counts").GetProperty("unresolved").GetInt32().Should().Be(2);
        list.GetProperty("counts").GetProperty("skipped").GetInt32().Should().Be(0);

        using var unresolvedResponse = await client.GetAsync(
            new Uri($"{ItemEndpoint}?importListId={listId}&state=unresolved", UriKind.Relative));
        var unresolved = await SongApiTests.ReadJsonAsync(unresolvedResponse);

        unresolvedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unresolved.GetProperty("totalRecords").GetInt32().Should().Be(2);
        unresolved.GetProperty("sortKey").GetString().Should().Be("line");

        var records = unresolved.GetProperty("records");
        records.GetArrayLength().Should().Be(2);
        records[0].GetProperty("line").GetInt32().Should().Be(3);
        records[0].GetProperty("text").GetString().Should().Be(MysteryLine);
        records[0].GetProperty("artist").GetString().Should().Be("Mystery");
        records[0].GetProperty("title").GetString().Should().Be("Song");
        records[0].GetProperty("state").GetString().Should().Be("unresolved");
        records[0].GetProperty("candidates").GetArrayLength().Should().Be(1);
        records[0].GetProperty("candidates")[0].GetProperty("mbRecordingId").GetString().Should().Be(MysteryRecording);
        records[0].GetProperty("candidates")[0].GetProperty("score").GetDouble().Should().BeApproximately(91.5, 0.001);

        records[1].GetProperty("line").GetInt32().Should().Be(4);
        records[1].GetProperty("text").GetString().Should().Be(AnotherLine);

        // Picking the candidate adds the song and clears the line's reason.
        var itemId = records[0].GetProperty("id").GetInt64();

        using var resolved = await client.PostAsync(
            new Uri($"{ItemEndpoint}/{itemId}/resolve", UriKind.Relative),
            SongApiTests.Json($$"""{"mbRecordingId":"{{MysteryRecording}}"}"""));

        resolved.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolvedItem = await SongApiTests.ReadJsonAsync(resolved);
        resolvedItem.GetProperty("state").GetString().Should().Be("added");
        resolvedItem.GetProperty("songId").GetInt64().Should().BeGreaterThan(0);
        resolvedItem.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);

        // Skipping the other one ends it.
        var otherId = records[1].GetProperty("id").GetInt64();

        using var skipped = await client.PostAsync(
            new Uri($"{ItemEndpoint}/{otherId}/skip", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        skipped.StatusCode.Should().Be(HttpStatusCode.OK);

        var skippedItem = await SongApiTests.ReadJsonAsync(skipped);
        skippedItem.GetProperty("state").GetString().Should().Be("skipped");
        skippedItem.GetProperty("reason").GetString().Should().Be("Skipped by the user");

        using var after = await client.GetAsync(new Uri($"{ListEndpoint}/{listId}", UriKind.Relative));
        var counts = (await SongApiTests.ReadJsonAsync(after)).GetProperty("counts");

        counts.GetProperty("added").GetInt32().Should().Be(3);
        counts.GetProperty("unresolved").GetInt32().Should().Be(0);
        counts.GetProperty("skipped").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Resolving_a_line_neither_provider_knows_is_a_not_found()
    {
        using var factory = SongApiTests.FakeProviders(Resolver());
        using var client = SongApiTests.Authenticated(factory);

        using var posted = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new { text = MysteryLine })));
        var listId = (await SongApiTests.ReadJsonAsync(posted)).GetProperty("importListId").GetInt64();

        using var itemsResponse = await client.GetAsync(
            new Uri($"{ItemEndpoint}?importListId={listId}", UriKind.Relative));
        var itemId = (await SongApiTests.ReadJsonAsync(itemsResponse))
            .GetProperty("records")[0].GetProperty("id").GetInt64();

        using var moved = await client.PostAsync(
            new Uri($"{ItemEndpoint}/{itemId}/resolve", UriKind.Relative),
            SongApiTests.Json($$"""{"mbRecordingId":"{{UnknownRecording}}"}"""));

        moved.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var both = await client.PostAsync(
            new Uri($"{ItemEndpoint}/{itemId}/resolve", UriKind.Relative),
            SongApiTests.Json($$"""{"mbRecordingId":"{{MysteryRecording}}","deezerId":700}"""));

        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var neither = await client.PostAsync(
            new Uri($"{ItemEndpoint}/{itemId}/resolve", UriKind.Relative),
            SongApiTests.Json("{}"));

        neither.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var unknown = await client.PostAsync(
            new Uri($"{ItemEndpoint}/987654/skip", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_empty_or_oversized_list_is_a_bad_request_and_an_unknown_list_is_a_not_found()
    {
        using var factory = SongApiTests.FakeProviders(Substitute.For<IIdentityResolver>());
        using var client = SongApiTests.Authenticated(factory);

        using var empty = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json("""{"text":"   "}"""));

        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SongApiTests.ReadJsonAsync(empty)).GetProperty("detail").GetString().Should().Contain("text");

        var tooMany = string.Join(
            '\n',
            Enumerable.Range(1, 1001).Select(index => $"Artist {index} - Title {index}"));

        using var oversized = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new { text = tooMany })));

        oversized.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SongApiTests.ReadJsonAsync(oversized)).GetProperty("detail").GetString().Should().Contain("1000");

        using var unknownProfile = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json($$"""{"text":"{{MysteryLine}}","qualityProfileId":987654}"""));

        unknownProfile.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var missingList = await client.GetAsync(new Uri($"{ListEndpoint}/987654", UriKind.Relative));
        missingList.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_bulk_endpoint_requires_the_api_key()
    {
        using var factory = SongApiTests.FakeProviders(Substitute.For<IIdentityResolver>());
        using var client = factory.CreateClient();

        using var bulk = await client.PostAsync(
            new Uri(BulkEndpoint, UriKind.Relative),
            SongApiTests.Json("""{"text":"Queen - Bohemian Rhapsody"}"""));

        bulk.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var list = await client.GetAsync(new Uri($"{ListEndpoint}/1", UriKind.Relative));
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var items = await client.GetAsync(new Uri(ItemEndpoint, UriKind.Relative));
        items.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var resolve = await client.PostAsync(
            new Uri($"{ItemEndpoint}/1/resolve", UriKind.Relative),
            SongApiTests.Json("{}"));

        resolve.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var skip = await client.PostAsync(
            new Uri($"{ItemEndpoint}/1/skip", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        skip.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The resolver every line of these tests goes through; nothing reaches a network.</summary>
    private static IIdentityResolver Resolver()
    {
        var resolver = Substitute.For<IIdentityResolver>();

        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<string>() switch
            {
                QueenLine => new ResolveResult { Status = ResolveStatus.Resolved, Identity = Identity(QueenRecording, "Bohemian Rhapsody", "Queen") },
                NobodyLine => new ResolveResult { Status = ResolveStatus.ResolvedDeezerOnly, Identity = DeezerIdentity(700, "Nothing", "Nobody") },
                _ => new ResolveResult
                {
                    Status = ResolveStatus.Unresolved,
                    Reason = "No match on MusicBrainz or Deezer",
                    Candidates =
                    [
                        new SongCandidate
                        {
                            Source = "musicbrainz",
                            MbRecordingId = MysteryRecording,
                            Title = "Mystery Song",
                            ArtistCredit = "Mystery",
                            DurationMs = 180_000,
                            Score = 91.5,
                        },
                    ],
                },
            }));

        // What the review screen's resolve does: the picked id is read back as a full identity.
        resolver
            .GetIdentityAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var recording = call.ArgAt<string?>(0);

                if (recording == MysteryRecording)
                {
                    return Task.FromResult<SongIdentity?>(Identity(MysteryRecording, "Mystery Song", "Mystery"));
                }

                return Task.FromResult<SongIdentity?>(
                    recording is null && call.ArgAt<long?>(1) == 700
                        ? DeezerIdentity(700, "Nothing", "Nobody")
                        : null);
            });

        return resolver;
    }

    /// <summary>One MusicBrainz identity with a single official album, so the song service can file it.</summary>
    private static SongIdentity Identity(string mbRecordingId, string title, string artist) => new()
    {
        Source = IdentityResolver.MusicBrainzSource,
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = artist,
        Artists = [new IdentityArtist(artist, artist, null, null, Core.Domain.ArtistRole.Main, 0)],
        DurationMs = 180_000,
        OriginalDate = "1975-10-31",
        ReleaseOptions =
        [
            new ReleaseOption
            {
                Key = $"release-{mbRecordingId}",
                MbReleaseId = $"release-{mbRecordingId}",
                Title = "A Night at the Opera",
                AlbumArtist = artist,
                PrimaryType = "Album",
                Status = "Official",
                Date = "1975-10-31",
            },
        ],
    };

    /// <summary>One Deezer-only identity.</summary>
    private static SongIdentity DeezerIdentity(long deezerId, string title, string artist) => new()
    {
        Source = IdentityResolver.DeezerSource,
        DeezerId = deezerId,
        Title = title,
        ArtistCredit = artist,
        Artists = [new IdentityArtist(artist, artist, null, null, Core.Domain.ArtistRole.Main, 0)],
        DurationMs = 120_000,
    };

    /// <summary>Polls a command until it reaches a terminal status.</summary>
    private static async Task<JsonElement> WaitForTerminalStatusAsync(HttpClient client, long id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var last = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(new Uri($"{CommandEndpoint}/{id}", UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            last = await SongApiTests.ReadJsonAsync(response);

            if (last.GetProperty("status").GetString() is "completed" or "failed" or "aborted")
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Command {id} never finished; last seen as {last}");
    }
}
