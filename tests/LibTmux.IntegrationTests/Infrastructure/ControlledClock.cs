namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>A clock that moves only when a test moves it, and says when a timer is armed on it.</summary>
/// <remarks>
/// A wait driven by this clock does not depend on how long tmux or a process
/// launch takes: its elapsed time and its timers stand still until the test
/// advances them, and the test advances them once the wait reports that it is
/// ready.
/// </remarks>
internal sealed class ControlledClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private readonly List<Waiter> _waiters = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _ticks);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Returns a task that completes when a timer is next armed for a matching span.</summary>
    /// <param name="match">Chooses the armed span.</param>
    /// <returns>The task, taken before the timer can be armed.</returns>
    internal Task<ArmedTimer> NextArmedAsync(Func<TimeSpan, bool> match)
    {
        var waiter = new Waiter(match);
        lock (_gate)
        {
            _waiters.Add(waiter);
        }

        return waiter.Source.Task;
    }

    /// <summary>Moves the clock forward, firing each timer that falls due on the way, earliest first.</summary>
    /// <param name="span">How far to move.</param>
    internal void Advance(TimeSpan span)
    {
        long target = Volatile.Read(ref _ticks) + span.Ticks;
        while (true)
        {
            ClockTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(timer => timer.Due <= target)
                    .MinBy(static timer => timer.Due);
                if (next is null)
                {
                    Volatile.Write(ref _ticks, target);
                    return;
                }

                Volatile.Write(ref _ticks, Math.Max(Volatile.Read(ref _ticks), next.Due));
            }

            next.Fire();
        }
    }

    private void Armed(ClockTimer timer)
    {
        Waiter[] matched;
        lock (_gate)
        {
            matched = [.. _waiters.Where(waiter => waiter.Match(timer.Span))];
            _ = _waiters.RemoveAll(waiter => matched.Contains(waiter));
        }

        foreach (Waiter waiter in matched)
        {
            _ = waiter.Source.TrySetResult(new ArmedTimer(timer));
        }
    }

    /// <summary>A timer armed on the clock, which can be fired before its time.</summary>
    /// <param name="Timer">The timer.</param>
    internal sealed record ArmedTimer(ITimer Timer)
    {
        /// <summary>Gets the span the timer was armed for.</summary>
        internal TimeSpan Span => ((ClockTimer)Timer).Span;

        /// <summary>Fires the timer now, with the clock where it is.</summary>
        internal void FireEarly() => ((ClockTimer)Timer).Fire();
    }

    private sealed record Waiter(Func<TimeSpan, bool> Match)
    {
        internal TaskCompletionSource<ArmedTimer> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ClockTimer(ControlledClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; private set; }

        internal TimeSpan Span { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _ = owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                Span = dueTime;
                Due = Volatile.Read(ref owner._ticks) + dueTime.Ticks;
                owner._timers.Add(this);
            }

            owner.Armed(this);
            return true;
        }

        internal void Fire()
        {
            lock (owner._gate)
            {
                _ = owner._timers.Remove(this);
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _ = owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
