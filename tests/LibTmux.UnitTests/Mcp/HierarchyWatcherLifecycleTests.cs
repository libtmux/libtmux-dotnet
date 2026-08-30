using System.Runtime.Versioning;
using System.Threading.Channels;
using LibTmux.Mcp;
using Microsoft.Extensions.Logging;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class HierarchyWatcherLifecycleTests
{
    // A leaked subscription would otherwise hang the run rather than name itself.
    private static readonly TimeSpan UnsubscribeTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Initial_recovery_invalidates_the_sole_subscriber_without_a_later_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var delay = new ControlledDelay();
        await using HierarchyWatcher watcher = new(null, delay.WaitAsync);
        FakeControlModeSession recovered = new();
        TaskCompletionSource restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            if (starts++ == 0)
            {
                return Task.FromException<IControlModeSession>(
                    new LibTmuxException("expected start failure"));
            }

            restarted.TrySetResult();
            return Task.FromResult<IControlModeSession>(recovered);
        }

        object subscriber = new();
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            changed =>
            {
                told.TrySetResult(changed);
                return Task.CompletedTask;
            },
            Start,
            token);

        Assert.Equal(TimeSpan.FromMilliseconds(100), await delay.Entered.Task.WaitAsync(token));
        Assert.Equal(1, starts);
        delay.Release();
        await restarted.Task.WaitAsync(token);
        Assert.Equal(["tmux://hierarchy"], await told.Task.WaitAsync(token));
        Assert.Equal(2, starts);

        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);
        await recovered.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, recovered.DisposeCalls);
    }

    [Fact]
    public async Task Stream_death_recovery_invalidates_every_resource_without_a_later_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var delay = new ControlledDelay();
        await using HierarchyWatcher watcher = new(null, delay.WaitAsync);
        FakeControlModeSession first = new();
        FakeControlModeSession recovered = new();
        TaskCompletionSource restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        int deliveries = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ == 0 ? first : recovered;
            if (starts == 2)
            {
                restarted.TrySetResult();
            }

            return Task.FromResult(session);
        }

        object subscriber = new();
        await watcher.SubscribeAsync(
            "tmux://sessions",
            subscriber,
            changed =>
            {
                Interlocked.Increment(ref deliveries);
                told.TrySetResult(changed);
                return Task.CompletedTask;
            },
            Start,
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            _ => Task.CompletedTask,
            Start,
            token);

        first.EndUnexpectedly();
        await first.Disposed.Task.WaitAsync(token);
        Assert.Equal(TimeSpan.FromMilliseconds(100), await delay.Entered.Task.WaitAsync(token));
        Assert.Equal(1, starts);
        delay.Release();
        await restarted.Task.WaitAsync(token);

        IReadOnlyList<string> changed = await told.Task.WaitAsync(token);
        Assert.Equal(2, changed.Count);
        Assert.Equal(1, changed.Count(uri => uri == "tmux://sessions"));
        Assert.Equal(1, changed.Count(uri => uri == "tmux://hierarchy"));
        Assert.Equal(1, Volatile.Read(ref deliveries));
        Assert.Equal(2, starts);

        await watcher.UnsubscribeAsync("tmux://sessions", subscriber);
        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);
        await recovered.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, recovered.DisposeCalls);
    }

    [Fact]
    public async Task Stale_failed_recovery_cannot_rearm_after_a_new_live_run()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var delay = new ControlledDelay();
        var outcome = new ControlledBarrier();
        TaskCompletionSource<bool> outcomeObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using HierarchyWatcher watcher = new(
            null,
            delay.WaitAsync,
            outcome.WaitAsync,
            pending => outcomeObserved.TrySetResult(pending));
        FakeControlModeSession first = new();
        FakeControlModeSession recovered = new();
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        int deliveries = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            int attempt = Interlocked.Increment(ref starts);
            return attempt switch
            {
                1 => Task.FromResult<IControlModeSession>(first),
                2 => Task.FromException<IControlModeSession>(
                    new LibTmuxException("expected recovery failure")),
                3 => Task.FromResult<IControlModeSession>(recovered),
                _ => Task.FromException<IControlModeSession>(
                    new InvalidOperationException("The watcher started too many clients.")),
            };
        }

        object subscriber = new();
        Func<IReadOnlyList<string>, Task> announce = changed =>
        {
            Interlocked.Increment(ref deliveries);
            told.TrySetResult(changed);
            return Task.CompletedTask;
        };
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            announce,
            Start,
            token);

        first.EndUnexpectedly();
        await first.Disposed.Task.WaitAsync(token);
        await delay.Entered.Task.WaitAsync(token);
        delay.Release();
        await outcome.Entered.Task.WaitAsync(token);

        await watcher.SubscribeAsync(
            "tmux://sessions",
            subscriber,
            announce,
            Start,
            token);
        IReadOnlyList<string> recoveredResources = await told.Task.WaitAsync(token);
        Assert.Equal(2, recoveredResources.Count);
        Assert.Equal(1, recoveredResources.Count(uri => uri == "tmux://hierarchy"));
        Assert.Equal(1, recoveredResources.Count(uri => uri == "tmux://sessions"));
        Assert.Equal(1, Volatile.Read(ref deliveries));

        outcome.Release();
        Assert.False(await outcomeObserved.Task.WaitAsync(token));
        await watcher.SubscribeAsync(
            "tmux://servers",
            subscriber,
            announce,
            Start,
            token);

        Assert.Equal(1, Volatile.Read(ref deliveries));
        Assert.Equal(3, Volatile.Read(ref starts));

        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);
        await watcher.UnsubscribeAsync("tmux://sessions", subscriber);
        await watcher.UnsubscribeAsync("tmux://servers", subscriber);
        await recovered.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, recovered.DisposeCalls);
    }

    [Fact]
    public async Task Disposal_cancels_a_pending_recovery()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var delay = new ControlledDelay();
        HierarchyWatcher watcher = new(null, delay.WaitAsync);
        int starts = 0;

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            _ =>
            {
                starts++;
                return Task.FromException<IControlModeSession>(
                    new LibTmuxException("expected start failure"));
            },
            token);

        await delay.Entered.Task.WaitAsync(token);
        await watcher.DisposeAsync().AsTask().WaitAsync(token);

        await delay.Cancelled.Task.WaitAsync(token);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Concurrent_disposal_joins_endpoint_cleanup()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        HierarchyWatcher watcher = new();
        FakeControlModeSession session = new(pauseDisposal: true);

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            _ => Task.FromResult<IControlModeSession>(session),
            token);

        Task first = watcher.DisposeAsync().AsTask();
        await session.DisposeStarted.Task.WaitAsync(token);
        Task second = watcher.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        session.AllowDisposal();
        await Task.WhenAll(first, second).WaitAsync(token);
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public async Task Cancelled_duplicate_cannot_remove_a_concurrent_subscription()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource firstCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession replacement = new();
        TaskCompletionSource firstStartEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;

        async Task<IControlModeSession> Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStartEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }

            return replacement;
        }

        object subscriber = new();
        Func<IReadOnlyList<string>, Task> announce = changed =>
        {
            told.TrySetResult(changed);
            return Task.CompletedTask;
        };
        Task first = watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            announce,
            Start,
            firstCancellation.Token);
        await firstStartEntered.Task.WaitAsync(token);
        Task second = watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            announce,
            Start,
            token);

        Assert.False(second.IsCompleted);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second.WaitAsync(token);
        replacement.Publish(new TmuxNotificationEvent("window-add", ["@1"]));

        Assert.Equal(["tmux://hierarchy"], await told.Task.WaitAsync(token));
        Assert.Equal(2, starts);

        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);
        await replacement.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, replacement.DisposeCalls);
    }

    [Fact]
    public async Task Duplicate_subscriptions_are_one_reference_and_one_delivery()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession session = new();
        object subscriber = new();
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        int duplicateDeliveries = 0;

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            changed =>
            {
                told.TrySetResult(changed);
                return Task.CompletedTask;
            },
            _ =>
            {
                starts++;
                return Task.FromResult<IControlModeSession>(session);
            },
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            _ =>
            {
                Interlocked.Increment(ref duplicateDeliveries);
                return Task.CompletedTask;
            },
            _ =>
            {
                starts++;
                return Task.FromResult<IControlModeSession>(session);
            },
            token);

        Assert.False(told.Task.IsCompleted);
        session.Publish(new TmuxNotificationEvent("window-add", ["@1"]));

        Assert.Equal(["tmux://hierarchy"], await told.Task.WaitAsync(token));
        Assert.Equal(1, starts);
        Assert.Equal(0, Volatile.Read(ref duplicateDeliveries));

        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);
        await session.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public async Task One_logical_unsubscribe_retires_every_generation_it_crossed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        object subscriber = new();

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            _ => Task.CompletedTask,
            "endpoint",
            new ServerGeneration(1, 101),
            _ => Task.FromResult<IControlModeSession>(first),
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            subscriber,
            _ => Task.CompletedTask,
            "endpoint",
            new ServerGeneration(2, 202),
            _ => Task.FromResult<IControlModeSession>(second),
            token);

        await watcher.UnsubscribeAsync("tmux://hierarchy", subscriber);

        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task A_later_subscription_restarts_after_the_control_stream_ends()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ switch
            {
                0 => first,
                1 => second,
                _ => throw new InvalidOperationException("The watcher started too many clients."),
            };
            return Task.FromResult(session);
        }

        object firstSubscriber = new();
        object secondSubscriber = new();
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            firstSubscriber,
            _ => Task.CompletedTask,
            Start,
            token);

        first.EndUnexpectedly();
        await first.Disposed.Task.WaitAsync(token);

        await watcher.SubscribeAsync(
            "tmux://sessions",
            secondSubscriber,
            _ => Task.CompletedTask,
            Start,
            token);

        Assert.Equal(2, starts);
        Assert.Equal(["refresh-client -f ignore-size,no-output"], first.Commands);
        Assert.Equal(["refresh-client -f ignore-size,no-output"], second.Commands);

        await watcher.UnsubscribeAsync("tmux://hierarchy", firstSubscriber);
        await watcher.UnsubscribeAsync("tmux://sessions", secondSubscriber);
        await second.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task Restart_waits_for_the_dead_client_cleanup_without_losing_the_subscriber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        HierarchyWatcher watcher = new();
        FakeControlModeSession first = new(pauseDisposal: true);
        FakeControlModeSession second = new();
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ == 0 ? first : second;
            return Task.FromResult(session);
        }

        object firstSubscriber = new();
        object secondSubscriber = new();
        try
        {
            await watcher.SubscribeAsync(
                "tmux://hierarchy",
                firstSubscriber,
                _ => Task.CompletedTask,
                Start,
                token);

            first.EndUnexpectedly();
            await first.DisposeStarted.Task.WaitAsync(token);

            Task restart = watcher.SubscribeAsync(
                "tmux://sessions",
                secondSubscriber,
                _ => Task.CompletedTask,
                Start,
                token);
            Assert.Equal(1, starts);
            Assert.False(restart.IsCompleted);

            first.AllowDisposal();
            await restart.WaitAsync(token);
            Assert.Equal(2, starts);

            await watcher.UnsubscribeAsync("tmux://hierarchy", firstSubscriber);
            await watcher.UnsubscribeAsync("tmux://sessions", secondSubscriber);
            await second.Disposed.Task.WaitAsync(token);
            Assert.Equal(1, first.DisposeCalls);
            Assert.Equal(1, second.DisposeCalls);
        }
        finally
        {
            first.AllowDisposal();
            await watcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Different_endpoint_generations_own_separate_runs_and_invalidations()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        object firstSubscriber = new();
        object secondSubscriber = new();
        TaskCompletionSource<IReadOnlyList<string>> firstTold = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> secondTold = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int firstStarts = 0;
        int secondStarts = 0;

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            firstSubscriber,
            changed =>
            {
                firstTold.TrySetResult(changed);
                return Task.CompletedTask;
            },
            "endpoint-a",
            new ServerGeneration(101, 1001),
            _ =>
            {
                firstStarts++;
                return Task.FromResult<IControlModeSession>(first);
            },
            token);
        await watcher.SubscribeAsync(
            "tmux://sessions",
            secondSubscriber,
            changed =>
            {
                secondTold.TrySetResult(changed);
                return Task.CompletedTask;
            },
            "endpoint-b",
            new ServerGeneration(202, 2002),
            _ =>
            {
                secondStarts++;
                return Task.FromResult<IControlModeSession>(second);
            },
            token);

        Assert.Equal(1, firstStarts);
        Assert.Equal(1, secondStarts);
        Assert.Equal(["refresh-client -f ignore-size,no-output"], first.Commands);
        Assert.Equal(["refresh-client -f ignore-size,no-output"], second.Commands);

        first.Publish(new TmuxNotificationEvent("window-add", ["@1"]));
        Assert.Equal(
            ["tmux://hierarchy"],
            await firstTold.Task.WaitAsync(token));
        Assert.False(secondTold.Task.IsCompleted);

        second.Publish(new TmuxNotificationEvent("window-add", ["@1"]));
        Assert.Equal(
            ["tmux://sessions"],
            await secondTold.Task.WaitAsync(token));

        await watcher.UnsubscribeAsync("tmux://hierarchy", firstSubscriber);
        await first.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(0, second.DisposeCalls);

        await watcher.UnsubscribeAsync("tmux://sessions", secondSubscriber);
        await second.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task A_stuck_subscriber_does_not_starve_peers_or_disposal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        HierarchyWatcher watcher = new();
        FakeControlModeSession session = new();
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> secondTold = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await watcher.SubscribeAsync(
                "tmux://hierarchy",
                new object(),
                async _ =>
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.ConfigureAwait(false);
                    firstFinished.TrySetResult();
                },
                _ => Task.FromResult<IControlModeSession>(session),
                token);
            await watcher.SubscribeAsync(
                "tmux://sessions",
                new object(),
                changed =>
                {
                    secondTold.TrySetResult(changed);
                    return Task.CompletedTask;
                },
                _ => Task.FromResult<IControlModeSession>(session),
                token);

            session.Publish(new TmuxNotificationEvent("window-add", ["@1"]));

            await firstStarted.Task.WaitAsync(token);
            Assert.Equal(["tmux://sessions"], await secondTold.Task.WaitAsync(token));
            await watcher.DisposeAsync().AsTask().WaitAsync(token);
            Assert.Equal(1, session.DisposeCalls);
            Assert.False(firstFinished.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
            if (firstStarted.Task.IsCompleted)
            {
                await firstFinished.Task.WaitAsync(token);
            }

            await watcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_faulted_subscriber_is_observed_without_stopping_peers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger();
        await using HierarchyWatcher watcher = new(logger);
        FakeControlModeSession session = new();
        TaskCompletionSource<IReadOnlyList<string>> peerTold = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.FromException(new InvalidOperationException("subscriber failed")),
            _ => Task.FromResult<IControlModeSession>(session),
            token);
        await watcher.SubscribeAsync(
            "tmux://sessions",
            new object(),
            changed =>
            {
                peerTold.TrySetResult(changed);
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IControlModeSession>(session),
            token);

        session.Publish(new TmuxNotificationEvent("window-add", ["@1"]));

        Assert.Equal(["tmux://sessions"], await peerTold.Task.WaitAsync(token));
        (EventId EventId, Exception Error) failure = await logger.Failure.Task.WaitAsync(token);
        Assert.Equal(10, failure.EventId.Id);
        Assert.IsType<InvalidOperationException>(failure.Error);
    }

    [Fact]
    public async Task Keyless_unsubscribe_releases_the_resource_on_every_endpoint()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ switch
            {
                0 => first,
                1 => second,
                _ => throw new InvalidOperationException("The watcher started too many clients."),
            };
            return Task.FromResult(session);
        }

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            "endpoint-a",
            new ServerGeneration(11, 111),
            Start,
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            "endpoint-b",
            new ServerGeneration(12, 121),
            Start,
            token);

        Assert.Equal(2, starts);

        await watcher.UnsubscribeAsync("tmux://hierarchy");

        await first.Disposed.Task.WaitAsync(UnsubscribeTimeout, token);
        await second.Disposed.Task.WaitAsync(UnsubscribeTimeout, token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task Keyless_unsubscribe_releases_every_holder_of_one_resource()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using HierarchyWatcher watcher = new();
        FakeControlModeSession session = new();
        ServerGeneration generation = new(13, 131);

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            "endpoint-a",
            generation,
            _ => Task.FromResult<IControlModeSession>(session),
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            new object(),
            _ => Task.CompletedTask,
            "endpoint-a",
            generation,
            _ => Task.FromResult<IControlModeSession>(session),
            token);

        await watcher.UnsubscribeAsync("tmux://hierarchy");

        await session.Disposed.Task.WaitAsync(UnsubscribeTimeout, token);
        Assert.Equal(1, session.DisposeCalls);
    }

    private sealed class FakeControlModeSession : IControlModeSession
    {
        private readonly Channel<TmuxEvent> _events = Channel.CreateUnbounded<TmuxEvent>();
        private readonly TaskCompletionSource? _allowDisposal;
        private int _disposeCalls;
        private int _disposed;
        private int _running = 1;

        internal FakeControlModeSession(bool pauseDisposal = false)
        {
            if (pauseDisposal)
            {
                _allowDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal List<string> Commands { get; } = [];

        internal TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public IAsyncEnumerable<TmuxEvent> Events => _events.Reader.ReadAllAsync();

        public bool IsRunning => Volatile.Read(ref _running) != 0;

        public Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(string.Join(' ', command.ToArguments()));
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref _running, 0);
                _events.Writer.TryComplete();
                DisposeStarted.TrySetResult();
                if (_allowDisposal is not null)
                {
                    await _allowDisposal.Task.ConfigureAwait(false);
                }

                Disposed.TrySetResult();
            }
        }

        internal void AllowDisposal() => _allowDisposal?.TrySetResult();

        internal void EndUnexpectedly()
        {
            Volatile.Write(ref _running, 0);
            _events.Writer.TryComplete();
        }

        internal void Publish(TmuxEvent observed) => _events.Writer.TryWrite(observed);
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<TimeSpan> Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Cancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(delay);
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ControlledBarrier
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal TaskCompletionSource<(EventId EventId, Exception Error)> Failure { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Failure.TrySetResult((eventId, exception));
            }
        }
    }
}
