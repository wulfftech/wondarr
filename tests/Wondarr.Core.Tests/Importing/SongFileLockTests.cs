using FluentAssertions;
using Wondarr.Core.Importing;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The per-song file guard on its own: one song at a time, different songs side by side, and a
/// cancelled wait that gives its reference back.
/// </summary>
public sealed class SongFileLockTests
{
    [Fact]
    public async Task Two_holders_of_one_song_never_overlap()
    {
        var songLock = new SongFileLock();

        var first = await songLock.AcquireAsync(1, CancellationToken.None);
        var second = songLock.AcquireAsync(1, CancellationToken.None).AsTask();

        second.IsCompleted.Should().BeFalse("the song's lock is held");
        songLock.EntryCount.Should().Be(1);

        await first.DisposeAsync();

        var handle = await second;
        await handle.DisposeAsync();

        songLock.EntryCount.Should().Be(0, "the last holder released the song's entry");
    }

    [Fact]
    public async Task Different_songs_do_not_block_each_other()
    {
        var songLock = new SongFileLock();

        var first = await songLock.AcquireAsync(1, CancellationToken.None);
        var second = await songLock.AcquireAsync(2, CancellationToken.None);

        songLock.EntryCount.Should().Be(2);

        await first.DisposeAsync();
        await second.DisposeAsync();

        songLock.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_wait_gives_its_reference_back()
    {
        var songLock = new SongFileLock();

        var holder = await songLock.AcquireAsync(1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = songLock.AcquireAsync(1, cancellation.Token).AsTask();

        cancellation.Cancel();

        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        songLock.EntryCount.Should().Be(1, "the holder still holds the song");

        await holder.DisposeAsync();

        songLock.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task Never_two_holders_under_contention()
    {
        // A sanity check under contention. The specific interleaving that once allowed two holders (the
        // last holder dropping the entry between a newcomer's lookup and its count) is too narrow to
        // hit by chance; it is closed by construction — the lookup and the count share one lock.
        var songLock = new SongFileLock();
        var inside = 0;
        var overlaps = 0;

        async Task WorkAsync()
        {
            for (var round = 0; round < 2000; round++)
            {
                await using (await songLock.AcquireAsync(7, CancellationToken.None))
                {
                    if (Interlocked.Increment(ref inside) > 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    Interlocked.Decrement(ref inside);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(WorkAsync)));

        overlaps.Should().Be(0);
        songLock.EntryCount.Should().Be(0);
    }
}
