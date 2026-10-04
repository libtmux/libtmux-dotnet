using System.Runtime.Versioning;
using System.Threading.Channels;
using LibTmux.Internal;
using LibTmux.Mcp;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PaneActivityHubLifecycleTests
{
    [Fact]
    public async Task Notification_loss_wakes_every_observed_pane_in_the_session()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession session = new();
        await using IAsyncDisposable lease = await hub.WatchAsync(
            "$1", _ => Task.FromResult<IControlModeSession>(session), cancellationToken: token);
        Task first = Assert.IsAssignableFrom<Task>(hub.CaptureSignal("%1"));
        object second = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%2"));

        session.Emit(new TmuxEventsDroppedEvent(2, 2));
        session.Emit(new TmuxOutputEvent(new PaneId(2), "barrier"));
        Assert.True(await hub.WaitForActivityAsync(
            "%2", second, TimeSpan.FromSeconds(1), token, lease));

        Assert.True(first.IsCompleted);
        Assert.True(hub.IsStreaming);
        Assert.Equal(2, PaneActivityHub.EventsDropped(lease));
        Assert.False(PaneActivityHub.RequireObservation(lease, hub.CaptureSignal("%1")));
    }

    [Fact]
    public async Task Each_watch_discloses_only_losses_after_its_lease_started()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession session = new();
        await using IAsyncDisposable first = await hub.WatchAsync(
            "$1", _ => Task.FromResult<IControlModeSession>(session), cancellationToken: token);
        object firstSignal = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%1"));
        session.Emit(new TmuxEventsDroppedEvent(2, 2));
        Assert.True(await hub.WaitForActivityAsync(
            "%1", firstSignal, TimeSpan.FromSeconds(1), token, first));

        await using IAsyncDisposable second = await hub.WatchAsync(
            "$1", _ => throw new InvalidOperationException("A live watch is shared."), cancellationToken: token);
        object secondSignal = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%1"));
        session.Emit(new TmuxEventsDroppedEvent(3, 5));
        Assert.True(await hub.WaitForActivityAsync(
            "%1", secondSignal, TimeSpan.FromSeconds(1), token, second));

        Assert.Equal(5, PaneActivityHub.EventsDropped(first));
        Assert.Equal(3, PaneActivityHub.EventsDropped(second));
    }

    [Fact]
    public async Task Control_attach_failure_does_not_silently_enable_polling()
    {
        await using PaneActivityHub hub = new();
        LibTmuxException failure = new("The control client could not attach.");

        LibTmuxException observed = await Assert.ThrowsAsync<LibTmuxException>(() =>
            hub.WatchAsync(
                "$1",
                _ => Task.FromException<IControlModeSession>(failure),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
        Assert.False(hub.IsStreaming);
    }

    [Fact]
    public async Task Losing_control_does_not_silently_enable_polling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession session = new();
        await using IAsyncDisposable lease = await hub.WatchAsync(
            "$1",
            _ => Task.FromResult<IControlModeSession>(session),
            cancellationToken: token);
        object signal = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%1"));

        session.EndUnexpectedly();
        await session.Disposed.Task.WaitAsync(token);
        Assert.True(await hub.WaitForActivityAsync(
            "%1", signal, TimeSpan.FromSeconds(1), token, lease));
        Assert.Null(hub.CaptureSignal("%1"));
        await Assert.ThrowsAsync<TmuxTransportException>(() => hub.WaitForActivityAsync(
            "%1", null, TimeSpan.FromSeconds(1), token, lease));
    }

    [Fact]
    public async Task Write_tools_leave_a_supplied_activity_hub_alive()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        using var accessor = new TmuxConnectionAccessor(Server.Open(
            new ServerConnectionOptions { SocketName = "supplied-tools" }));
        var tools = new WriteTools(accessor, new ServerPolicy(), hub);

        await Assert.IsAssignableFrom<IAsyncDisposable>(tools).DisposeAsync();

        FakeControlModeSession session = new();
        await using IAsyncDisposable lease = await hub.WatchAsync(
            "$1",
            _ => Task.FromResult<IControlModeSession>(session),
            cancellationToken: token);
        Assert.True(hub.IsStreaming);
    }

    [Fact]
    public async Task A_later_watch_restarts_after_the_control_stream_ends()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ switch
            {
                0 => first,
                1 => second,
                _ => throw new InvalidOperationException("The hub started too many clients."),
            };
            return Task.FromResult(session);
        }

        IAsyncDisposable firstLease = await hub.WatchAsync("$1", Start, cancellationToken: token);
        Assert.True(hub.IsStreaming);
        object signal = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%1"));

        // Invalid pane identifiers cannot receive stream events. A strict
        // lease rejects their missing signals instead of polling.
        Assert.Null(hub.CaptureSignal("not-a-pane"));
        Assert.Null(hub.CaptureSignal("@1"));
        Assert.Throws<TmuxTransportException>(() =>
            PaneActivityHub.RequireObservation(firstLease, hub.CaptureSignal("not-a-pane")));
        first.Emit(new TmuxOutputEvent(new PaneId(1), "changed"));
        Assert.True(await hub.WaitForActivityAsync(
            "%1",
            signal,
            TimeSpan.FromSeconds(1),
            token,
            firstLease));

        first.EndUnexpectedly();
        await first.Disposed.Task.WaitAsync(token);
        Assert.False(hub.IsStreaming);
        Assert.Null(hub.CaptureSignal("%1"));

        IAsyncDisposable secondLease = await hub.WatchAsync("$1", Start, cancellationToken: token);
        Assert.True(hub.IsStreaming);
        Assert.Equal(2, starts);
        Assert.Throws<TmuxTransportException>(() =>
            PaneActivityHub.RequireObservation(firstLease, hub.CaptureSignal("%1")));
        Assert.Equal(
            ["refresh-client -f ignore-size", "refresh-client -B libtmux-pane-dead:%*:#{pane_dead}", "display-message -p #{client_name}"],
            first.Commands);
        Assert.Equal(
            ["refresh-client -f ignore-size", "refresh-client -B libtmux-pane-dead:%*:#{pane_dead}", "display-message -p #{client_name}"],
            second.Commands);

        await firstLease.DisposeAsync();
        await secondLease.DisposeAsync();
        await second.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task Restart_waits_for_dead_client_cleanup_without_losing_the_watch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        PaneActivityHub hub = new();
        FakeControlModeSession first = new(pauseDisposal: true);
        FakeControlModeSession second = new();
        List<IAsyncDisposable> leases = [];
        int starts = 0;

        Task<IControlModeSession> Start(CancellationToken _)
        {
            IControlModeSession session = starts++ == 0 ? first : second;
            return Task.FromResult(session);
        }

        try
        {
            leases.Add(await hub.WatchAsync("$1", Start, cancellationToken: token));
            first.EndUnexpectedly();
            await first.DisposeStarted.Task.WaitAsync(token);
            Assert.False(hub.IsStreaming);

            Task<IAsyncDisposable> restart = hub.WatchAsync("$1", Start, cancellationToken: token);
            Assert.Equal(1, starts);
            Assert.False(restart.IsCompleted);

            first.AllowDisposal();
            leases.Add(await restart.WaitAsync(token));
            Assert.Equal(2, starts);
            Assert.True(hub.IsStreaming);

            foreach (IAsyncDisposable lease in leases)
            {
                await lease.DisposeAsync();
            }

            await second.Disposed.Task.WaitAsync(token);
            Assert.Equal(1, first.DisposeCalls);
            Assert.Equal(1, second.DisposeCalls);
        }
        finally
        {
            first.AllowDisposal();
            foreach (IAsyncDisposable lease in leases)
            {
                await lease.DisposeAsync();
            }

            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task Last_release_cannot_retire_a_watch_during_a_new_acquisition()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        PaneActivityHub hub = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();
        TaskCompletionSource secondStartEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IControlModeSession> allowSecondStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable? firstLease = null;
        IAsyncDisposable? secondLease = null;
        int starts = 0;

        async Task<IControlModeSession> Start(CancellationToken _)
        {
            if (starts++ == 0)
            {
                return first;
            }

            secondStartEntered.TrySetResult();
            return await allowSecondStart.Task.ConfigureAwait(false);
        }

        try
        {
            firstLease = await hub.WatchAsync("$1", Start, cancellationToken: token);
            first.EndUnexpectedly();
            await first.Disposed.Task.WaitAsync(token);

            Task<IAsyncDisposable> acquiring = hub.WatchAsync("$1", Start, cancellationToken: token);
            await secondStartEntered.Task.WaitAsync(token);
            Task releasing = firstLease.DisposeAsync().AsTask();
            await releasing.WaitAsync(token);
            Assert.False(acquiring.IsCompleted);

            allowSecondStart.TrySetResult(second);
            secondLease = await acquiring.WaitAsync(token);

            Assert.True(hub.IsStreaming);
            Assert.Equal(2, starts);
            Assert.Equal(0, second.DisposeCalls);

            await secondLease.DisposeAsync();
            await second.Disposed.Task.WaitAsync(token);
            Assert.Equal(1, second.DisposeCalls);
        }
        finally
        {
            allowSecondStart.TrySetResult(second);
            if (firstLease is not null)
            {
                await firstLease.DisposeAsync();
            }

            if (secondLease is not null)
            {
                await secondLease.DisposeAsync();
            }

            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_start_cannot_remove_a_concurrent_retry_watch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession replacement = new();
        TaskCompletionSource failingStartEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IControlModeSession> finishFailingStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int replacementStarts = 0;

        async Task<IControlModeSession> Fail(CancellationToken _)
        {
            failingStartEntered.TrySetResult();
            return await finishFailingStart.Task.ConfigureAwait(false);
        }

        Task<IControlModeSession> Retry(CancellationToken _)
        {
            replacementStarts++;
            return Task.FromResult<IControlModeSession>(replacement);
        }

        Task<IAsyncDisposable> failed = hub.WatchAsync(
            "$1", Fail, allowPollingFallback: true, cancellationToken: token);
        await failingStartEntered.Task.WaitAsync(token);
        Task<IAsyncDisposable> retry = hub.WatchAsync("$1", Retry, cancellationToken: token);

        finishFailingStart.TrySetException(new LibTmuxException("expected start failure"));
        await using IAsyncDisposable unavailable = await failed.WaitAsync(token);
        await using IAsyncDisposable acquired = await retry.WaitAsync(token);

        Assert.True(PaneActivityHub.FallbackAtAcquisition(unavailable));
        Assert.True(PaneActivityHub.RequireObservation(unavailable, null));
        Assert.Equal(1, replacementStarts);
        Assert.True(hub.IsStreaming);

        await acquired.DisposeAsync();
        await replacement.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, replacement.DisposeCalls);
    }

    [Fact]
    public async Task Unavailable_session_falls_back_only_for_its_permissive_lease()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession streaming = new();

        await using IAsyncDisposable streamingLease = await hub.WatchAsync(
            "endpoint-a",
            "$1",
            _ => Task.FromResult<IControlModeSession>(streaming),
            cancellationToken: token);
        await using IAsyncDisposable unavailableLease = await hub.WatchAsync(
            "endpoint-b",
            "$1",
            _ => Task.FromException<IControlModeSession>(
                new LibTmuxException("expected start failure")),
            allowPollingFallback: true, cancellationToken: token);

        Task streamingSignal = Assert.IsAssignableFrom<Task>(
            hub.CaptureSignal("endpoint-a", "$1", "%1"));
        object? unavailableSignal = hub.CaptureSignal("endpoint-b", "$1", "%1");
        Assert.Null(unavailableSignal);
        Assert.False(PaneActivityHub.RequireObservation(streamingLease, streamingSignal));
        Assert.True(PaneActivityHub.FallbackAtAcquisition(unavailableLease));
        Assert.True(PaneActivityHub.RequireObservation(unavailableLease, unavailableSignal));

        bool activity = await hub.WaitForActivityAsync(
                "%1",
                unavailableSignal,
                TimeSpan.FromSeconds(1),
                token,
                unavailableLease)
            .WaitAsync(TimeSpan.FromSeconds(1), token);

        Assert.False(activity);
    }

    [Fact]
    public async Task Equal_ids_on_different_endpoints_do_not_share_signals()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession first = new();
        FakeControlModeSession second = new();

        await using IAsyncDisposable firstLease = await hub.WatchAsync(
            "endpoint-a",
            "$1",
            _ => Task.FromResult<IControlModeSession>(first),
            cancellationToken: token);
        await using IAsyncDisposable secondLease = await hub.WatchAsync(
            "endpoint-b",
            "$1",
            _ => Task.FromResult<IControlModeSession>(second),
            cancellationToken: token);

        object firstSignal = Assert.IsAssignableFrom<object>(
            hub.CaptureSignal("endpoint-a", "$1", "%1"));
        object secondSignal = Assert.IsAssignableFrom<object>(
            hub.CaptureSignal("endpoint-b", "$1", "%1"));
        Task<bool> firstWait = hub.WaitForActivityAsync(
            "%1",
            firstSignal,
            TimeSpan.FromSeconds(1),
            token,
            firstLease);
        Task<bool> secondWait = hub.WaitForActivityAsync(
            "%1",
            secondSignal,
            TimeSpan.FromSeconds(1),
            token,
            secondLease);

        first.Emit(new TmuxOutputEvent(new PaneId(1), "first"));
        Assert.True(await firstWait.WaitAsync(token));
        Assert.False(secondWait.IsCompleted);

        second.Emit(new TmuxOutputEvent(new PaneId(1), "second"));
        Assert.True(await secondWait.WaitAsync(token));
    }

    [Fact]
    public async Task Explicit_fallback_is_observable_after_a_stream_is_lost()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession session = new();
        await using IAsyncDisposable lease = await hub.WatchAsync(
            "$1", _ => Task.FromResult<IControlModeSession>(session), allowPollingFallback: true, cancellationToken: token);
        await using IAsyncDisposable strictLease = await hub.WatchAsync(
            "$1", _ => throw new InvalidOperationException("A live watch is shared."), cancellationToken: token);
        Assert.False(PaneActivityHub.RequireObservation(lease, hub.CaptureSignal("%1")));
        Assert.False(PaneActivityHub.RequireObservation(strictLease, hub.CaptureSignal("%1")));

        session.EndUnexpectedly();
        await session.Disposed.Task.WaitAsync(token);

        Assert.True(PaneActivityHub.RequireObservation(lease, hub.CaptureSignal("%1")));
        Assert.Throws<TmuxTransportException>(() =>
            PaneActivityHub.RequireObservation(strictLease, hub.CaptureSignal("%1")));
        Assert.False(await hub.WaitForActivityAsync(
            "%1", null, TimeSpan.FromMilliseconds(1), token, lease));
    }

    [Fact]
    public async Task Explicit_fallback_never_consumes_startup_cancellation()
    {
        using CancellationTokenSource cancellation = new();
        await using PaneActivityHub hub = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hub.WatchAsync(
            "$1", _ => Task.FromCanceled<IControlModeSession>(cancellation.Token),
            allowPollingFallback: true, cancellationToken: cancellation.Token));
        Assert.False(hub.IsStreaming);
    }

    // A client that ended on its own, such as on a line over its limits,
    // fails its disposal; the wait releasing it has its result already.
    [Fact]
    public async Task Releasing_a_watch_whose_client_failed_does_not_fail_the_release()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FailingDisposalSession failing = new();

        IAsyncDisposable lease = await hub.WatchAsync(
            "$1",
            _ => Task.FromResult<IControlModeSession>(failing),
            cancellationToken: token);
        Assert.True(hub.IsStreaming);

        await lease.DisposeAsync();
        Assert.True(failing.Disposed);
    }

    // One client failing its disposal must not leave the hub's other clients
    // attached when the hub is disposed.
    [Fact]
    public async Task Disposing_the_hub_disposes_every_client_when_one_fails()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        PaneActivityHub hub = new();
        FailingDisposalSession failing = new();
        FakeControlModeSession healthy = new();
        _ = await hub.WatchAsync("$1", _ => Task.FromResult<IControlModeSession>(failing), cancellationToken: token);
        _ = await hub.WatchAsync("$2", _ => Task.FromResult<IControlModeSession>(healthy), cancellationToken: token);

        await hub.DisposeAsync();

        Assert.True(failing.Disposed);
        Assert.Equal(1, healthy.DisposeCalls);
    }

    private sealed class FailingDisposalSession : IControlModeSession
    {
        private readonly Channel<TmuxEvent> _events = Channel.CreateUnbounded<TmuxEvent>();

        internal bool Disposed { get; private set; }

        public IAsyncEnumerable<TmuxEvent> Events => _events.Reader.ReadAllAsync();

        public bool IsRunning => !Disposed;

        public Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _events.Writer.TryComplete();
            throw new TmuxProtocolException(
                "A tmux control-mode line exceeded 65536 bytes.",
                TmuxDispatchState.Unknown);
        }
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

        internal void Emit(TmuxEvent item) => _events.Writer.TryWrite(item);

        internal void EndUnexpectedly()
        {
            Volatile.Write(ref _running, 0);
            _events.Writer.TryComplete();
        }
    }
}
