using System.Runtime.Versioning;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeDisposalDeadlineTests
{
    [Fact]
    public async Task Disposal_starts_forced_cleanup_before_one_async_deadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var clock = new ManualTimerTimeProvider();
        var process = new ConcurrentCleanupProcess();
        var session = new ControlModeSession(
            process,
            disposalBudget: TimeSpan.FromSeconds(10),
            timeProvider: clock);
        await session.WaitForReadyAsync(token);

        Task disposal = session.DisposeAsync().AsTask();
        await process.ExitWaitStarted.WaitAsync(TimeSpan.FromSeconds(1), token);
        Assert.True(process.CloseInputCalled);

        clock.Advance(TimeSpan.FromSeconds(5));
        await process.KillStarted.WaitAsync(TimeSpan.FromSeconds(1), token);
        await process.ErrorPumpStopStarted.WaitAsync(TimeSpan.FromSeconds(1), token);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Equal(2, clock.TimersCreated);
        Assert.False(process.DisposeCalled);

        clock.Advance(TimeSpan.FromSeconds(5));
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            () => disposal.WaitAsync(TimeSpan.FromSeconds(2), token));

        Assert.Equal(
            "Control-mode asynchronous cleanup exceeded its disposal deadline.",
            error.Message);
        Assert.True(process.DisposeCalled);
        process.FailPending(new IOException("late cleanup failure"));
    }

    [Fact]
    public async Task Disposal_preserves_a_cleanup_fault_at_the_deadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var clock = new ManualTimerTimeProvider();
        var process = new ConcurrentCleanupProcess();
        var session = new ControlModeSession(
            process,
            disposalBudget: TimeSpan.FromSeconds(10),
            timeProvider: clock);
        await session.WaitForReadyAsync(token);

        Task disposal = session.DisposeAsync().AsTask();
        await process.ExitWaitStarted.WaitAsync(TimeSpan.FromSeconds(1), token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await process.ErrorPumpStopStarted.WaitAsync(TimeSpan.FromSeconds(1), token);
        var cleanupFailure = new IOException("error pump failed at the boundary");
        process.FailErrorPump(cleanupFailure);

        clock.Advance(TimeSpan.FromSeconds(5));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            () => disposal.WaitAsync(TimeSpan.FromSeconds(2), token));

        Assert.Contains(cleanupFailure, error.InnerExceptions);
        Assert.Contains(error.InnerExceptions, failure => failure is TimeoutException);
        process.FailExit(new IOException("late exit failure"));
    }

    private sealed class ConcurrentCleanupProcess : IControlModeProcess
    {
        private readonly TaskCompletionSource _errorPumpStop = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _errorPumpStopStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exitWaitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _killStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<string> _output = new(
            ["%begin 1 1 0", "%end 1 1 0"]);
        private readonly TaskCompletionSource<string?> _terminalOutput = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool CloseInputCalled { get; private set; }

        internal bool DisposeCalled { get; private set; }

        internal Task ErrorPumpStopStarted => _errorPumpStopStarted.Task;

        internal Task ExitWaitStarted => _exitWaitStarted.Task;

        internal Task KillStarted => _killStarted.Task;

        public bool HasExited => false;

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The timeout probe does not dispatch commands.");

        public Task FlushAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The timeout probe does not dispatch commands.");

        public Task<string?> ReadLineAsync() => _output.TryDequeue(out string? line)
            ? Task.FromResult<string?>(line)
            : _terminalOutput.Task;

        public void CloseInput() => CloseInputCalled = true;

        public void Kill() => _killStarted.TrySetResult();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            _exitWaitStarted.TrySetResult();
            return _exit.Task;
        }

        public Task StopErrorPumpAsync(CancellationToken cancellationToken)
        {
            _errorPumpStopStarted.TrySetResult();
            return _errorPumpStop.Task;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            _terminalOutput.TrySetResult(null);
        }

        internal void FailErrorPump(Exception failure) =>
            _errorPumpStop.TrySetException(failure);

        internal void FailExit(Exception failure) => _exit.TrySetException(failure);

        internal void FailPending(Exception failure)
        {
            _errorPumpStop.TrySetException(failure);
            _exit.TrySetException(failure);
        }
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
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
}
