namespace LibTmux.UnitTests.ControlMode;

/// <summary>A clock whose timers fire only when a test advances it.</summary>
internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    internal TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _elapsed;
            }
        }
    }

    internal int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            Change(timer, dueTime, period);
            return timer;
        }
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _elapsed += duration;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.Active && timer.DueAt <= _elapsed)
                {
                    timer.Active = timer.Period != Timeout.InfiniteTimeSpan;
                    if (timer.Active)
                    {
                        timer.DueAt += timer.Period;
                    }

                    callbacks.Add((timer.Callback, timer.State));
                }
            }
        }

        foreach ((TimerCallback callback, object? state) in callbacks)
        {
            callback(state);
        }
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        timer.Active = dueTime != Timeout.InfiniteTimeSpan;
        timer.DueAt = timer.Active ? _elapsed + dueTime : TimeSpan.MaxValue;
        timer.Period = period;
    }

    private sealed class ManualTimer(
        ManualTimerTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal bool Active { get; set; }

        internal TimerCallback Callback { get; } = callback;

        internal TimeSpan DueAt { get; set; }

        internal TimeSpan Period { get; set; }

        internal object? State { get; } = state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner.Change(this, dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Active = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
