using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Waiting;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxWaitChannelTests
{
    private static readonly TimeSpan Attempt = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    [UnixFact]
    public async Task An_expired_attempt_still_sees_a_signal_that_lands_afterwards()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        const string Channel = "libtmux-survives";

        TmuxWaitChannel wait = server.OpenWaitChannel(Channel);

        // Nothing has signalled yet, so the attempt expires. The waiter stays
        // registered, which is the whole point: tmux hands a signal to whoever
        // is registered, and a waiter killed to enforce a timeout eats it.
        Assert.False(await wait.WaitAsync(Attempt, token));

        await server.WaitForAsync(new WaitForRequest(Channel, TmuxWaitMode.Signal), token);

        Assert.True(await wait.WaitAsync(Arrival, token));
        await wait.DisposeAsync();

        // Disposal signals the channel to withdraw, so it must not turn a wait
        // that really was signalled into one that merely stopped.
        Assert.True(wait.Signalled);
    }

    [UnixFact]
    public async Task Withdrawing_a_wait_reports_it_was_never_signalled()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        const string Channel = "libtmux-withdrawn";

        TmuxWaitChannel abandoned = server.OpenWaitChannel(Channel);
        Assert.False(await abandoned.WaitAsync(Attempt, token));
        await abandoned.DisposeAsync();
        Assert.False(abandoned.Signalled);

        // Withdrawing signals the channel to deregister, so it must not leave
        // the channel looking as though something had really signalled it.
        await using TmuxWaitChannel next = server.OpenWaitChannel(Channel);
        Assert.False(await next.WaitAsync(Attempt, token));
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
