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
        endpoint.FailWait();
        _ = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(1), token));
        endpoint.ReleaseWithdrawal();

        _ = await Assert.ThrowsAsync<TmuxTransportException>(
            () => wait.DisposeAsync().AsTask());

        Assert.Equal(1, endpoint.SignalCount);
        Assert.False(endpoint.HasWaiter);
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

    [Fact]
    public async Task A_signal_racing_withdrawal_is_not_attributed_and_stays_pending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var endpoint = new WaitChannelEndpoint();
        Server server = endpoint.Server;
        using var accessor = new TmuxConnectionAccessor(server);
        await using var activity = new PaneActivityHub();
        await using var jobs = new JobStore();
        var tools = new WriteTools(
            accessor,
            new ServerPolicy(),
            activity,
            jobs);

        Task<ActionResult> timingOut = tools.WaitForChannelAsync(
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

        ActionResult raced = await timingOut.WaitAsync(token);
        Assert.Contains("cannot tell whether a signal raced", raced.Changed, StringComparison.Ordinal);

        ActionResult next = await tools.WaitForChannelAsync(
            "attribution-race",
            timeoutSeconds: 1,
            cancellationToken: token);
        Assert.Equal("Channel 'attribution-race' was signalled.", next.Changed);
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

        internal WaitChannelEndpoint()
        {
            var connection = new TmuxConnection(
                new ServerConnectionOptions(socketName: "wait-attribution"),
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            Server = new Server(connection, new ServerGeneration(17, 29), "tmux 3.7");
        }

        internal Server Server { get; }

        internal Task WithdrawalStarted => _withdrawalStarted.Task;

        internal int SignalCount => Volatile.Read(ref _signals);

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

        internal void FailWait()
        {
            lock (_gate)
            {
                if (_waiter is null)
                {
                    throw new InvalidOperationException("No waiter is registered.");
                }

                _waiter.TrySetException(new TmuxTransportException(
                    "The waiting client failed.",
                    ["wait-for"]));
            }
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

        private Task<TmuxCommandResult> WaitAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_pending)
                {
                    _pending = false;
                    return Task.FromResult(Success(arguments));
                }

                _waiter = new TaskCompletionSource<TmuxCommandResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiter.Task.WaitAsync(cancellationToken);
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
    }
}
