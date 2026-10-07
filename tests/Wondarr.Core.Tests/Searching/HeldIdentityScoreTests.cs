using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// What <c>SongSearchService.BuildContextAsync</c> makes of the held file's stored candidate
/// (MATCHING_ENGINE §6.6): the identity rule only has teeth when the held file's identity sub-score
/// is known, and nothing about an unreadable column may fail the search.
/// </summary>
public sealed class HeldIdentityScoreTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_known_held_score_rejects_a_worse_identity_upgrade_and_keeps_a_matching_one()
    {
        var (host, songId) = await HostAsync();
        await using var _ = host;

        var candidateId = await host.SeedCandidateAsync(songId, """{"title":200,"artist":100,"duration":100,"identity":390,"quality":300,"availability":150,"sourcePreference":100,"adjustments":[],"adjustmentTotal":0,"total":940,"cappedForUnknownDuration":false}""");
        await host.SeedFileAsync(
            songId,
            qualityId: 29,
            sourceRef: JsonSerializer.Serialize(new HeldRef("peer", "Music\\held.mp3", candidateId), Json));

        host.Provider.Candidates.Add(Clean());
        host.Provider.Candidates.Add(Worse());

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        var stored = await host.Runs.GetCandidatesAsync(result.SearchRunId, Token);
        stored.Single(candidate => candidate.DisplayName == "Alpha.flac").Accepted.Should().BeTrue();
        stored.Single(candidate => candidate.DisplayName == "Alpha2.flac").Accepted.Should().BeFalse();
        stored.Single(candidate => candidate.DisplayName == "Alpha2.flac").Rejections.Should().Contain("worseIdentity");
    }

    [Fact]
    public async Task A_missing_stored_candidate_leaves_only_the_quality_rule()
    {
        var (host, songId) = await HostAsync();
        await using var _ = host;

        // The source ref points at a candidate row that was pruned: nothing can be said.
        await host.SeedFileAsync(
            songId,
            qualityId: 29,
            sourceRef: JsonSerializer.Serialize(new HeldRef("peer", "Music\\held.mp3", 999_999), Json));

        host.Provider.Candidates.Add(Clean());
        host.Provider.Candidates.Add(Worse());

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token))
            .Should().OnlyContain(candidate => candidate.Accepted);
    }

    [Fact]
    public async Task A_malformed_breakdown_leaves_only_the_quality_rule()
    {
        var (host, songId) = await HostAsync();
        await using var _ = host;

        var candidateId = await host.SeedCandidateAsync(songId, "not json at all");
        await host.SeedFileAsync(
            songId,
            qualityId: 29,
            sourceRef: JsonSerializer.Serialize(new HeldRef("peer", "Music\\held.mp3", candidateId), Json));

        host.Provider.Candidates.Add(Clean());
        host.Provider.Candidates.Add(Worse());

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token))
            .Should().OnlyContain(candidate => candidate.Accepted);
    }

    [Fact]
    public async Task A_file_without_a_source_ref_leaves_only_the_quality_rule()
    {
        var (host, songId) = await HostAsync();
        await using var _ = host;

        await host.SeedFileAsync(songId, qualityId: 29);

        host.Provider.Candidates.Add(Clean());
        host.Provider.Candidates.Add(Worse());

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token))
            .Should().OnlyContain(candidate => candidate.Accepted);
    }

    /// <summary>A song under the Lossless profile holding an MP3-320, so a FLAC is a strict upgrade.</summary>
    private static async Task<(SearchTestHost Host, long SongId)> HostAsync()
    {
        var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();

        await using (var context = host.Database.CreateContext(host.Time))
        {
            var song = await context.Songs.FirstAsync(song => song.Id == songId, Token);
            song.QualityProfileId = SeedData.LosslessProfileId;
            await context.SaveChangesAsync(Token);
        }

        return (host, songId);
    }

    /// <summary>A FLAC that matches the song as well as anything can.</summary>
    private static Candidate Clean() => SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");

    /// <summary>A FLAC that matches the song clearly less well than the held file's candidate did (more than the identity tolerance below it).</summary>
    private static Candidate Worse() => SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha2.flac") with
    {
        Parsed = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha2.flac").Parsed with { Title = "Omega" },
    };

    /// <summary>The shape <c>song_file.source_ref</c> has when a stored candidate produced the file.</summary>
    private sealed record HeldRef(string? Provider, string RemotePath, long CandidateId);
}
