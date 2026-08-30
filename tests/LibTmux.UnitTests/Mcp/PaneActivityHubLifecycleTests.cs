using System.Runtime.Versioning;
using System.Threading.Channels;
using LibTmux.Mcp;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PaneActivityHubLifecycleTests
{
    [Fact]
    public async Task Write_tools_leave_a_supplied_activity_hub_alive()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        await using JobStore jobs = new();
        using var accessor = new TmuxConnectionAccessor(Server.Open(
            new ServerConnectionOptions(socketName: "supplied-tools")));
        var tools = new WriteTools(accessor, new ServerPolicy(), hub, jobs);

        await Assert.IsAssignableFrom<IAsyncDisposable>(tools).DisposeAsync();

        FakeControlModeSession session = new();
        await using IAsyncDisposable lease = await hub.WatchAsync(
            "$1",
            _ => Task.FromResult<IControlModeSession>(session),
            token);
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

        IAsyncDisposable firstLease = await hub.WatchAsync("$1", Start, token);
        Assert.True(hub.IsStreaming);
        object signal = Assert.IsAssignableFrom<object>(hub.CaptureSignal("%1"));
        first.Emit(new TmuxOutputEvent("%1", "changed"));
        Assert.True(await hub.WaitForActivityAsync(
            "%1",
            signal,
            TimeSpan.FromSeconds(1),
            token));

        first.EndUnexpectedly();
        await first.Disposed.Task.WaitAsync(token);
        Assert.False(hub.IsStreaming);
        Assert.Null(hub.CaptureSignal("%1"));

        IAsyncDisposable secondLease = await hub.WatchAsync("$1", Start, token);
        Assert.True(hub.IsStreaming);
        Assert.Equal(2, starts);
        Assert.Equal(["refresh-client -f ignore-size"], first.Commands);
        Assert.Equal(["refresh-client -f ignore-size"], second.Commands);

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
            leases.Add(await hub.WatchAsync("$1", Start, token));
            first.EndUnexpectedly();
            await first.DisposeStarted.Task.WaitAsync(token);
            Assert.False(hub.IsStreaming);

            Task<IAsyncDisposable> restart = hub.WatchAsync("$1", Start, token);
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
            firstLease = await hub.WatchAsync("$1", Start, token);
            first.EndUnexpectedly();
            await first.Disposed.Task.WaitAsync(token);

            Task<IAsyncDisposable> acquiring = hub.WatchAsync("$1", Start, token);
            await secondStartEntered.Task.WaitAsync(token);
            Task releasing = firstLease.DisposeAsync().AsTask();
            Assert.False(releasing.IsCompleted);

            allowSecondStart.TrySetResult(second);
            secondLease = await acquiring.WaitAsync(token);
            await releasing.WaitAsync(token);

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

        Task<IAsyncDisposable> failed = hub.WatchAsync("$1", Fail, token);
        await failingStartEntered.Task.WaitAsync(token);
        Task<IAsyncDisposable> retry = hub.WatchAsync("$1", Retry, token);

        finishFailingStart.TrySetException(new LibTmuxException("expected start failure"));
        await using IAsyncDisposable unavailable = await failed.WaitAsync(token);
        await using IAsyncDisposable acquired = await retry.WaitAsync(token);

        Assert.Equal(1, replacementStarts);
        Assert.True(hub.IsStreaming);

        await acquired.DisposeAsync();
        await replacement.Disposed.Task.WaitAsync(token);
        Assert.Equal(1, replacement.DisposeCalls);
    }

    [Fact]
    public async Task Unavailable_session_polls_while_another_session_streams()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PaneActivityHub hub = new();
        FakeControlModeSession streaming = new();

        await using IAsyncDisposable streamingLease = await hub.WatchAsync(
            "endpoint-a",
            "$1",
            _ => Task.FromResult<IControlModeSession>(streaming),
            token);
        await using IAsyncDisposable unavailableLease = await hub.WatchAsync(
            "endpoint-b",
            "$1",
            _ => Task.FromException<IControlModeSession>(
                new LibTmuxException("expected start failure")),
            token);

        Assert.NotNull(hub.CaptureSignal("endpoint-a", "$1", "%1"));
        object? unavailableSignal = hub.CaptureSignal("endpoint-b", "$1", "%1");
        Assert.Null(unavailableSignal);

        bool activity = await hub.WaitForActivityAsync(
                "%1",
                unavailableSignal,
                TimeSpan.FromSeconds(5),
                token)
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
            token);
        await using IAsyncDisposable secondLease = await hub.WatchAsync(
            "endpoint-b",
            "$1",
            _ => Task.FromResult<IControlModeSession>(second),
            token);

        object firstSignal = Assert.IsAssignableFrom<object>(
            hub.CaptureSignal("endpoint-a", "$1", "%1"));
        object secondSignal = Assert.IsAssignableFrom<object>(
            hub.CaptureSignal("endpoint-b", "$1", "%1"));
        Task<bool> firstWait = hub.WaitForActivityAsync(
            "%1",
            firstSignal,
            TimeSpan.FromSeconds(1),
            token);
        Task<bool> secondWait = hub.WaitForActivityAsync(
            "%1",
            secondSignal,
            TimeSpan.FromSeconds(1),
            token);

        first.Emit(new TmuxOutputEvent("%1", "first"));
        Assert.True(await firstWait.WaitAsync(token));
        Assert.False(secondWait.IsCompleted);

        second.Emit(new TmuxOutputEvent("%1", "second"));
        Assert.True(await secondWait.WaitAsync(token));
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
