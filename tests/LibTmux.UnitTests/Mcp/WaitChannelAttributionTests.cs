using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class WaitChannelAttributionTests
{
    [Fact]
    public async Task Faulted_wait_is_still_withdrawn_during_disposal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel("faulted-wait");
        var failure = new TmuxTransportException(
            "The waiting client failed.",
            ["wait-for"]);
        endpoint.FailWait(failure, registrationRemains: true);
        TmuxTransportException observed = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(1), token));
        Assert.Same(failure, observed);
        endpoint.ReleaseWithdrawal();

        TmuxTransportException disposalFailure = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.DisposeAsync().AsTask());

        Assert.Same(failure, disposalFailure);
        Assert.Equal(1, endpoint.SignalCount);
        Assert.False(endpoint.HasWaiter);
    }

    [Fact]
    public async Task Not_dispatched_failure_does_not_seed_the_next_wait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        const string channel = "not-dispatched";
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel(channel);
        var failure = new TmuxTransportException(
            "The waiting client did not start.",
            ["wait-for"],
            TmuxDispatchState.NotDispatched);
        endpoint.FailWait(failure, registrationRemains: false);
        TmuxTransportException observed = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(1), token));
        Assert.Same(failure, observed);
        endpoint.ReleaseWithdrawal();

        TmuxTransportException disposalFailure = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.DisposeAsync().AsTask());

        Assert.Same(failure, disposalFailure);
        await AssertNoPendingSignalAsync(endpoint.Server, channel, token);
    }

    [Fact]
    public async Task Command_failure_does_not_seed_the_next_wait_or_escape_disposal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        const string channel = "command-failure";
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel(channel);
        var failure = new TmuxCommandException(
            "tmux refused the wait.",
            WaitChannelEndpoint.Failure(["wait-for", channel]));
        endpoint.FailWait(failure, registrationRemains: false);
        TmuxCommandException observed = await Assert.ThrowsAsync<TmuxCommandException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(1), token));
        Assert.Same(failure, observed);
        endpoint.ReleaseWithdrawal();

        await wait.DisposeAsync();

        await AssertNoPendingSignalAsync(endpoint.Server, channel, token);
    }

    [Fact]
    public async Task Concurrent_disposal_waits_for_the_same_withdrawal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel("concurrent-disposal");

        Task first = wait.DisposeAsync().AsTask();
        await endpoint.WithdrawalStarted.WaitAsync(token);
        Task second = wait.DisposeAsync().AsTask();
        try
        {
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            endpoint.ReleaseWithdrawal();
        }

        await Task.WhenAll(first, second).WaitAsync(token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_close_ends_the_shared_clients_and_reports_unknown_registration(bool disposeFirst)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel("cancel-close");
        using var cancellation = new CancellationTokenSource();
        Task first = disposeFirst ? wait.DisposeAsync().AsTask() : wait.CloseAsync(cancellation.Token).AsTask();
        await endpoint.WithdrawalStarted.WaitAsync(token);
        Task second = disposeFirst ? wait.CloseAsync(cancellation.Token).AsTask() : first;
        try
        {
            await cancellation.CancelAsync();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => second.WaitAsync(TimeSpan.FromMilliseconds(500), token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            Assert.Contains("remote wait registration is unknown", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, endpoint.ActiveWaits);
            Assert.Equal(1, endpoint.SignalCount);
            Assert.True(endpoint.HasWaiter);
            Assert.False(wait.Signalled);
        }
        finally
        {
            endpoint.ReleaseWithdrawal();
            try { await Task.WhenAll(first, second); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    [Trait("Tier", "Outer")]
    // Exercises the published one-second withdrawal deadline.
    public async Task Dispose_has_a_default_deadline_and_reports_unknown_registration()
    {
        var endpoint = new WaitChannelEndpoint();
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel("default-close-deadline");
        Task closing = wait.DisposeAsync().AsTask();
        await endpoint.WithdrawalStarted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() => closing.WaitAsync(
                TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken));
            Assert.Contains("remote wait registration is unknown", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, endpoint.ActiveWaits);
            Assert.Equal(1, endpoint.SignalCount);
            Assert.True(endpoint.HasWaiter);
        }
        finally
        {
            endpoint.ReleaseWithdrawal();
            try { await closing; }
            catch (TimeoutException) { }
        }
    }

    [Fact]
    public async Task Close_preserves_cancellation_unrelated_to_its_owned_lifetime()
    {
        var endpoint = new WaitChannelEndpoint();
        TmuxWaitChannel wait = endpoint.Server.OpenWaitChannel("unrelated-cancellation");
        var failure = new OperationCanceledException("An independent operation was cancelled.");
        endpoint.FailWait(failure, registrationRemains: true);
        OperationCanceledException waitFailure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Same(failure, waitFailure);
        endpoint.ReleaseWithdrawal();

        OperationCanceledException observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.CloseAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(failure, observed);
        Assert.Equal(1, endpoint.SignalCount);
        Assert.False(endpoint.HasWaiter);
    }

    [Fact]
    public async Task A_signal_racing_withdrawal_is_not_attributed_and_stays_pending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        Server server = endpoint.Server;
        using var accessor = new TmuxConnectionAccessor(server);
        await using var activity = new PaneActivityHub();
        var tools = new WriteTools(
            accessor,
            new ServerPolicy(),
            activity);

        Task<ChannelWaitResult> timingOut = tools.WaitForChannelAsync(
            "attribution-race",
            timeoutSeconds: 0.01,
            cancellationToken: token);
        await endpoint.WithdrawalStarted.WaitAsync(token);

        try
        {
            await server.WaitForAsync(
                new WaitForRequest("attribution-race", TmuxWaitMode.Signal),
                token);
        }
        finally
        {
            endpoint.ReleaseWithdrawal();
        }

        ChannelWaitResult raced = await timingOut.WaitAsync(token);
        Assert.Contains("cannot tell whether a signal raced", raced.Changed, StringComparison.Ordinal);

        // Telling a timeout from a signal must not need substring matching.
        Assert.False(raced.Signalled);

        ChannelWaitResult next = await tools.WaitForChannelAsync(
            "attribution-race",
            timeoutSeconds: 1,
            cancellationToken: token);
        Assert.Equal("Channel 'attribution-race' was signalled.", next.Changed);
        Assert.True(next.Signalled);
    }

    private static async Task AssertNoPendingSignalAsync(
        Server server,
        string channel,
        CancellationToken cancellationToken)
    {
        await using TmuxWaitChannel next = server.OpenWaitChannel(channel);
        Assert.False(await next.WaitAsync(
            TimeSpan.FromMilliseconds(10),
            cancellationToken));
    }

    private sealed class WaitChannelEndpoint
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _withdrawalStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWithdrawal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<TmuxCommandResult>? _waiter;
        private bool _pending;
        private int _signals;
        private int _activeWaits;

        internal WaitChannelEndpoint()
        {
            var connection = new TmuxConnection(
                new ServerConnectionOptions { SocketName = "wait-attribution" },
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            Server = new Server(connection, new ServerGeneration(17, 29), "tmux 3.7");
        }

        internal Server Server { get; }

        internal Task WithdrawalStarted => _withdrawalStarted.Task;

        internal int SignalCount => Volatile.Read(ref _signals);

        internal int ActiveWaits => Volatile.Read(ref _activeWaits);

        internal bool HasWaiter
        {
            get
            {
                lock (_gate)
                {
                    return _waiter is not null;
                }
            }
        }

        internal void FailWait(Exception failure, bool registrationRemains)
        {
            TaskCompletionSource<TmuxCommandResult> waiter;
            lock (_gate)
            {
                waiter = _waiter
                    ?? throw new InvalidOperationException("No waiter is registered.");
                if (!registrationRemains)
                {
                    _waiter = null;
                }
            }

            waiter.TrySetException(failure);
        }

        internal void ReleaseWithdrawal() => _releaseWithdrawal.TrySetResult();

        private async Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> arguments = request.LogicalArguments;
            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                if (arguments.Contains("-S", StringComparer.Ordinal))
                {
                    if (Interlocked.Increment(ref _signals) == 1)
                    {
                        _withdrawalStarted.TrySetResult();
                        await _releaseWithdrawal.Task.WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Signal(arguments);
                    return Success(arguments);
                }

                return await WaitAsync(arguments, cancellationToken).ConfigureAwait(false);
            }

            return Success(arguments);
        }

        private async Task<TmuxCommandResult> WaitAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Task<TmuxCommandResult> waiting;
            lock (_gate)
            {
                if (_pending)
                {
                    _pending = false;
                    return Success(arguments);
                }

                _waiter = new TaskCompletionSource<TmuxCommandResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waiting = _waiter.Task;
                Interlocked.Increment(ref _activeWaits);
            }

            try
            {
                return await waiting.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWaits);
            }
        }

        private void Signal(IReadOnlyList<string> arguments)
        {
            TaskCompletionSource<TmuxCommandResult>? waiter;
            lock (_gate)
            {
                waiter = _waiter;
                _waiter = null;
                if (waiter is null)
                {
                    _pending = true;
                }
            }

            waiter?.TrySetResult(Success(arguments));
        }

        private static TmuxCommandResult Success(IReadOnlyList<string> arguments) => new(
            arguments,
            0,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            [],
            []);

        internal static TmuxCommandResult Failure(IReadOnlyList<string> arguments) => new(
            arguments,
            1,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            [],
            ["wait failed"]);
    }
}
