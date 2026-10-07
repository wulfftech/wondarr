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
}