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

    // The command timeout runs on a clock the test holds, so how long tmux
    // takes to start decides nothing: only the test's advance can end a command.
    [UnixFact]
    public async Task The_command_timeout_ends_neither_a_queued_lock_nor_a_wait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        var clock = new ControlledClock();
        TimeSpan commandTimeout = TimeSpan.FromMilliseconds(100);
        Server server = await ConnectAsync(raw, token, commandTimeout, clock);
        const string Lock = "libtmux-lock-timeout";
        const string Signal = "libtmux-signal-timeout";

        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Lock), token);
        Task queued = server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Lock), token);
        Task waiting = server.WaitForAsync(new WaitForRequest(Signal, TmuxWaitMode.Wait), token);

        // Three command timeouts pass while both still wait for another client.
        clock.Advance(3 * commandTimeout);

        // A task the timeout had ended would carry its failure to these waits.
        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Unlock), token);
        await queued.WaitAsync(TimeSpan.FromSeconds(5), token);
        await server.WaitForAsync(new WaitForRequest(Lock, TmuxWaitMode.Unlock), token);
        await server.WaitForAsync(new WaitForRequest(Signal, TmuxWaitMode.Signal), token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    [UnixFact]
    public async Task The_command_timeout_ends_an_ordinary_command_that_does_not_answer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        var clock = new ControlledClock();
        TimeSpan commandTimeout = TimeSpan.FromMilliseconds(100);
        Server server = await ConnectAsync(raw, token, commandTimeout, clock);
        Task<ControlledClock.ArmedTimer> armed = clock.NextArmedAsync(span => span == commandTimeout);

        // wait-for answers only when another client signals, so this one does not.
        Task<TmuxCommandResult> silent = server.ExecuteCommandAsync(
            ["wait-for", "libtmux-silent-channel"], token);
        _ = await armed.WaitAsync(token);
        clock.Advance(commandTimeout);

        TmuxTransportException expired = await Assert.ThrowsAsync<TmuxTransportException>(() => silent);
        Assert.Contains("did not answer within", expired.Message, StringComparison.Ordinal);
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        TimeSpan? commandTimeout = null,
        TimeProvider? clock = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                CommandTimeout = commandTimeout,
                Clock = clock,
            },
            token);
}
