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
