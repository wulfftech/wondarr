using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// An automatic grab stops at the cutoff (owner question 20, as in Lidarr): "search now" counts as
/// automatic, and only the interactive search is free to grab anything.
/// </summary>
public sealed class SearchCutoffTests
{
    private const long Mp3320 = 29;
    private const long Mp3256 = 23;

    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task Search_now_on_a_song_at_its_cutoff_grabs_nothing_and_says_why()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, Mp3320);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var handler = new SongSearchCommandHandler(host.Search, host.SlotWait, NullLogger<SongSearchCommandHandler>.Instance);
        var message = await handler.ExecuteAsync(
            new CommandContext(1, $"{{\"name\":\"SongSearch\",\"songId\":{songId}}}", CommandTrigger.Manual, _ => Task.CompletedTask),
            Token);

        message.Should().Contain("Cancelled").And.Contain("Already at the cutoff").And.Contain("MP3-320");
        host.Provider.Requests.Should().BeEmpty("a song at its cutoff is not searched for");
        host.Provider.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_now_on_a_song_below_its_cutoff_upgrades_it()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, Mp3256);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        host.Provider.Grabs.Should().ContainSingle();
    }

    [Fact]
    public async Task An_interactive_grab_past_the_cutoff_still_works()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, Mp3320);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed, "the user picked it by hand");
        host.Provider.Grabs.Should().ContainSingle();
    }

    [Fact]
    public async Task The_interactive_search_still_lists_every_release_for_a_song_at_its_cutoff()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, Mp3320);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);

        result.Decisions.Should().ContainSingle().Which.Accepted.Should().BeTrue();
        host.Provider.Grabs.Should().BeEmpty("the interactive search only lists");
    }
}
