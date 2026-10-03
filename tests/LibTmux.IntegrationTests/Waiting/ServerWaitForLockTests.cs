using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Waiting;

[UnsupportedOSPlatform("windows")]
public sealed class ServerWaitForLockTests
{
    [UnixFact]
    public async Task A_pre_cancelled_token_throws_before_any_lock_is_requested()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        Server server = await ConnectAsync(raw, TestContext.Current.CancellationToken);
        using CancellationTokenSource already = new();
        await already.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.WaitForAsync(
                new WaitForRequest("libtmux-lock-precancelled", TmuxWaitMode.Lock),
                already.Token));
    }

    [UnixFact]
    public async Task A_cancelled_lock_wait_releases_the_lock_it_goes_on_to_acquire()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        const string Channel = "libtmux-lock-release";

        // The holder takes the mutex first, so the locker below queues behind
        // it instead of acquiring immediately.
        await server.WaitForAsync(new WaitForRequest(Channel, TmuxWaitMode.Lock), token);

        using CancellationTokenSource shortLived = new(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.WaitForAsync(
                new WaitForRequest(Channel, TmuxWaitMode.Lock),
                shortLived.Token));

        // Gives the abandoned locker's client time to be queued with tmux
        // before the holder below releases the mutex to it.
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        await server.WaitForAsync(new WaitForRequest(Channel, TmuxWaitMode.Unlock), token);

        // A canceled Lock wait keeps its tmux client running rather than
        // killing it, so a lock tmux later hands to that entry is released
        // again automatically, and a fresh locker can still get in.
        using CancellationTokenSource thirdLockerBudget = new(TimeSpan.FromSeconds(5));
        await server.WaitForAsync(
            new WaitForRequest(Channel, TmuxWaitMode.Lock),
            thirdLockerBudget.Token);
        await server.WaitForAsync(new WaitForRequest(Channel, TmuxWaitMode.Unlock), token);
    }

    [UnixFact]
    public async Task The_command_timeout_ends_neither_a_queued_lock_nor_a_wait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token, TimeSpan.FromMilliseconds(200));
        const string Lock = "libtmux-lock-timeout";
        const string Signal = "libtmux-signal-timeout";

        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Lock), token);
        Task queued = server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Lock), token);
        Task waiting = server.WaitForAsync(new WaitForRequest(Signal, TmuxWaitMode.Wait), token);

        // Three command timeouts pass while both still wait for another client.
        Task elapsed = Task.Delay(TimeSpan.FromMilliseconds(600), token);
        Assert.Same(elapsed, await Task.WhenAny(queued, waiting, elapsed));

        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Unlock), token);
        await queued.WaitAsync(TimeSpan.FromSeconds(5), token);
        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Unlock), token);
        await server.WaitForAsync(new WaitForRequest(Signal, TmuxWaitMode.Signal), token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        TimeSpan? commandTimeout = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                CommandTimeout = commandTimeout,
            },
            token);
}
