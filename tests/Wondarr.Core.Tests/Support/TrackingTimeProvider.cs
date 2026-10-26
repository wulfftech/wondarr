using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Wondarr.Testing;

/// <summary>
/// A fake clock that knows which one-shot timers (<c>Task.Delay</c>) are pending and when each is
/// due, so a test can tell "the code under test has run what the last step made due" from "it has
/// not yet" without sleeping in real time.
/// </summary>
/// <remarks>
/// This file is compiled into both Wondarr.Core.Tests and Wondarr.Sources.Tests (the second one
/// links it), so each assembly has its own internal copy.
/// </remarks>
internal sealed class TrackingTimeProvider : FakeTimeProvider
{
    /// <summary>
    /// The longest real time one step waits for the code under test to catch up. It only bounds a
    /// step whose work never re-arms a delay; a step that does is released by a signal.
    /// </summary>
    private static readonly TimeSpan SettleCeiling = TimeSpan.FromSeconds(2);

    /// <summary>How long a step waits for a re-armed delay once everything that fired is finished with.</summary>
    private static readonly TimeSpan RearmGrace = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly List<TrackedTimer> _live = [];
    private int _armed;

    public TrackingTimeProvider()
    {
    }

    public TrackingTimeProvider(DateTimeOffset start)
        : base(start)
    {
    }

    /// <summary>How many timers have been created so far.</summary>
    public int TimersArmed
    {
        get
        {
            lock (_gate)
            {
                return _armed;
            }
        }
    }

    /// <summary>How many timers are pending right now.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
            }
        }
    }

    /// <summary>Whether advancing the clock by <paramref name="step"/> will fire at least one pending timer.</summary>
    public bool AnyTimerDueWithin(TimeSpan step) => TimersDueWithin(step).Count > 0;

    /// <summary>How far the clock has to move for the first pending timer to fire, or null while none is pending.</summary>
    public TimeSpan? UntilNextTimer()
    {
        lock (_gate)
        {
            var now = GetUtcNow();
            var due = _live
                .Where(timer => timer.Due > now && timer.Due != DateTimeOffset.MaxValue)
                .Select(timer => (DateTimeOffset?)timer.Due)
                .Min();

            return due - now;
        }
    }

    /// <summary>The pending timers that advancing the clock by <paramref name="step"/> will fire.</summary>
    public IReadOnlyList<object> TimersDueWithin(TimeSpan step)
    {
        lock (_gate)
        {
            var horizon = GetUtcNow() + step;
            return _live.Where(timer => timer.Due <= horizon).Cast<object>().ToArray();
        }
    }

    /// <summary>Whether a timer from <see cref="TimersDueWithin"/> is still pending, i.e. its owner has not yet disposed it.</summary>
    public bool IsPending(object timer)
    {
        lock (_gate)
        {
            return _live.Contains((TrackedTimer)timer);
        }
    }

    /// <summary>
    /// Waits, bounded, until the code under test is parked on a fake-clock delay, or
    /// <paramref name="condition"/> holds. Advancing a clock nobody is waiting on only burns steps.
    /// </summary>
    public async Task WaitForPendingTimerAsync(Func<bool>? condition = null)
    {
        var waiting = Stopwatch.StartNew();

        while (PendingTimers == 0 && (condition is null || !condition()) && waiting.Elapsed < SettleCeiling)
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// Moves fake time forward by <paramref name="step"/>, then waits until the code under test has
    /// run everything that step made due and gone back to waiting on the fake clock: every delay that
    /// fired has been finished with, and a new one has been armed (or <paramref name="condition"/>
    /// holds). No fixed real-time sleep decides the outcome.
    /// </summary>
    public async Task AdvanceAndSettleAsync(TimeSpan step, Func<bool>? condition = null)
    {
        var armed = TimersArmed;
        var fired = TimersDueWithin(step);

        Advance(step);

        var settle = Stopwatch.StartNew();
        Stopwatch? finished = null;
        var quiet = 0;
        var lastCompleted = ThreadPool.CompletedWorkItemCount;

        while (settle.Elapsed < SettleCeiling)
        {
            if (condition is not null && condition())
            {
                return;
            }

            // Nothing was due: the clock moving woke nobody, so there is nothing to wait for.
            var finishedWith = fired.All(timer => !IsPending(timer));

            if (finishedWith)
            {
                finished ??= Stopwatch.StartNew();
            }

            var caughtUp = fired.Count == 0
                || (finishedWith && (TimersArmed > armed || finished!.Elapsed >= RearmGrace));

            // The delay's continuation can still be queued on the thread pool, and what it kicks off
            // (file and database I/O) completes there too: wait for a stretch in which the pool
            // neither has work queued nor finishes any. A step that fired nothing skips the stretch.
            var completed = ThreadPool.CompletedWorkItemCount;
            var idle = caughtUp && ThreadPool.PendingWorkItemCount == 0 && (fired.Count == 0 || completed == lastCompleted);
            lastCompleted = completed;
            quiet = idle ? quiet + 1 : 0;

            if (quiet >= (fired.Count == 0 ? 5 : 4))
            {
                return;
            }

            if (fired.Count == 0)
            {
                await Task.Yield();
            }
            else
            {
                // Blocking on purpose: a sleep that is not itself pool work keeps the count above honest.
                Thread.Sleep(1);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var inner = base.CreateTimer(callback, state, dueTime, period);
        var timer = new TrackedTimer(this, inner, dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : GetUtcNow() + dueTime);

        lock (_gate)
        {
            _armed++;
            _live.Add(timer);
        }

        return timer;
    }

    private void Forget(TrackedTimer timer)
    {
        lock (_gate)
        {
            _live.Remove(timer);
        }
    }

    private sealed class TrackedTimer(TrackingTimeProvider owner, ITimer inner, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime;
            return inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            owner.Forget(this);
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            owner.Forget(this);
            return inner.DisposeAsync();
        }
    }
}
