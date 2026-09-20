using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

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

    [UnixFact]
    public async Task Cancelling_close_reaps_owned_clients_when_the_borrowed_daemon_is_frozen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult identity = await raw.ExecuteAsync(["display-message", "-p", "#{pid}"], token);
        int daemonId = int.Parse(identity.StandardOutputText.Trim(), CultureInfo.InvariantCulture);
        using Process daemon = Process.GetProcessById(daemonId);
        var clients = new ConcurrentQueue<Process>();
        var waiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var withdrawalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TmuxProcessTransport(raw.TmuxBinaryPath, raw.BuildInvocationArguments([]), launcher: start =>
        {
            RawTmuxTestContext.ConfigureEnvironment(start);
            Process client = Process.Start(start) ?? throw new InvalidOperationException("The test client did not start.");
            clients.Enqueue(Process.GetProcessById(client.Id));
            int commandIndex = start.ArgumentList.IndexOf("wait-for");
            (start.ArgumentList[commandIndex + 1] == "-S" ? withdrawalStarted : waiterStarted).TrySetResult();
            return client;
        });
        Server server = new(new TmuxCommandDispatcher(transport));
        TmuxWaitChannel wait = server.OpenWaitChannel("frozen-close");
        Task? closing = null;
        bool frozen = false;
        try
        {
            await waiterStarted.Task.WaitAsync(token);
            frozen = true;
            await SignalProcessAsync(daemonId, "-STOP", token);
            using var cancellation = new CancellationTokenSource();
            closing = wait.CloseAsync(cancellation.Token).AsTask();
            await withdrawalStarted.Task.WaitAsync(token);

            await cancellation.CancelAsync();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                try
                {
                    await closing.WaitAsync(TimeSpan.FromMilliseconds(500), token);
                }
                catch (TimeoutException) when (!closing.IsCompleted)
                {
                    Assert.Fail($"Close remains pending; owned client exit states in launch order: {string.Join(", ", clients.Select(client => client.HasExited))}.");
                }
            });

            Assert.Contains("remote wait registration is unknown", failure.Message, StringComparison.Ordinal);
            Assert.Equal(2, clients.Count);
            Assert.All(clients, client => Assert.True(client.HasExited));
            Assert.False(daemon.HasExited);
            Assert.False(wait.Signalled);
        }
        finally
        {
            if (frozen)
            {
                await SignalProcessAsync(daemonId, "-CONT", CancellationToken.None);
            }

            try
            {
                await (closing ?? wait.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                foreach (Process client in clients)
                {
                    client.Dispose();
                }
            }
        }

        RawTmuxResult alive = await raw.ExecuteAsync(["list-sessions"], token);
        Assert.Equal(0, alive.ExitCode);
    }

    private static async Task SignalProcessAsync(int processId, string signal, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("/bin/kill") { UseShellExecute = false };
        start.ArgumentList.Add(signal);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The process signal did not start.");
        await process.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
}
