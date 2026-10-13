using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Searching;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The process-wide slot gate guards "is a slot free?" together with the queue item that takes it, and
/// is not held while a source works on the grab (R-035): a YouTube grab runs the whole of yt-dlp there.
/// </summary>
public sealed class SlotGateTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_slow_grab_does_not_hold_up_another_songs_grab()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var slow = await host.SeedSongAsync("Alpha");
        var fast = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        // The first grab to reach the source stays inside it until the test lets it go, as a yt-dlp
        // download would; the second returns at once.
        var grabStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        host.Provider.GrabHook = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                grabStarted.SetResult();
                await release.Task;
            }
        };

        var slowSearch = Task.Run(() => host.Search.SearchAsync(slow, SearchTrigger.Automatic, grab: true, Token));
        await grabStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Another search, in its own scope as another command would have, grabs while the first is still going.
        using var scope = host.CreateScope();
        var other = await scope.ServiceProvider.GetRequiredService<ISongSearchService>()
            .SearchAsync(fast, SearchTrigger.Automatic, grab: true, Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        other.Outcome.Should().Be(SearchOutcome.Grabbed, "the gate is not held across the first song's download");
        slowSearch.IsCompleted.Should().BeFalse();

        release.SetResult();
        (await slowSearch.WaitAsync(TimeSpan.FromSeconds(10))).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_song_being_searched_by_one_run_is_left_alone_by_another()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        // Another run (the scheduled and the bodied missing search overlapping) is in the middle of
        // searching the song.
        using var scope = host.CreateScope();
        var waiters = scope.ServiceProvider.GetRequiredService<SlotWaiters>();
        waiters.TryBeginSearch(songId).Should().BeTrue();
        waiters.IsSearching(songId).Should().BeTrue();

        var skipped = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        skipped.Outcome.Should().Be(SearchOutcome.Cancelled);
        skipped.Message.Should().Be("Already being searched");
        host.Provider.Requests.Should().BeEmpty("the song is left to the run that has it");

        // The batch loops count it as skipped and go on.
        var handler = new MissingSearchCommandHandler(
            host.Scopes,
            host.Monitor,
            host.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MissingSearchCommandHandler>.Instance);
        var message = await handler.ExecuteAsync(
            new Wondarr.Core.Jobs.CommandContext(1, null, Wondarr.Core.Jobs.CommandTrigger.Scheduled, _ => Task.CompletedTask),
            Token);

        message.Should().Be("1 songs: 0 grabbed, 0 without an acceptable result, 1 skipped");

        // Once the other run lets go, the song is searched like any other.
        waiters.EndSearch(songId);
        (await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token)).Outcome
            .Should().Be(SearchOutcome.Grabbed);
        waiters.IsSearching(songId).Should().BeFalse("the claim is let go when the search ends");
    }

    [Fact]
    public async Task A_search_that_fails_lets_the_song_go()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.SearchFailure = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<SlotWaiters>().IsSearching(songId).Should().BeFalse();
    }

    [Fact]
    public async Task The_slot_a_slow_grab_took_still_counts_against_the_limit_while_it_downloads()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.MaxActiveDownloads = 1);
        var slow = await host.SeedSongAsync("Alpha");
        var other = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var grabStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        host.Provider.GrabHook = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                grabStarted.SetResult();
                await release.Task;
            }
        };

        var slowSearch = Task.Run(() => host.Search.SearchAsync(slow, SearchTrigger.Automatic, grab: true, Token));
        await grabStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The queue item is written before the source is asked, and it holds the only slot.
        using var scope = host.CreateScope();
        var blocked = await scope.ServiceProvider.GetRequiredService<ISongSearchService>()
            .SearchAsync(other, SearchTrigger.Automatic, grab: true, Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        blocked.QueueItemId.Should().BeNull("the one download slot is in use");
        blocked.Message.Should().Be("No free download slot");

        release.SetResult();
        (await slowSearch.WaitAsync(TimeSpan.FromSeconds(10))).Outcome.Should().Be(SearchOutcome.Grabbed);

        await using var context = host.Database.CreateContext(host.Time);
        (await context.QueueItems.AsNoTracking().CountAsync(Token)).Should().Be(1);
    }
}
