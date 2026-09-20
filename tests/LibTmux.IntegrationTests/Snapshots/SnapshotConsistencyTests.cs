using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class SnapshotConsistencyTests
{
    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("session", "list-windows")]
    [InlineData("window", "list-windows")]
    [InlineData("placement", "list-panes")]
    [InlineData("pane", "list-panes")]
    public async Task Contradictory_rows_do_not_publish_a_complete_capture(string change, string intercept)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        bool changed = false;
        Server server = await CreateInterceptedServer(raw, async (startInfo, cancellationToken) =>
        {
            if (!changed && startInfo.ArgumentList.Contains(intercept))
            {
                changed = true;
                string[] mutation = change switch
                {
                    "session" => ["new-session", "-d", "-s", "added"],
                    "window" => ["new-window", "-d", "-t", "$0:5"],
                    "placement" => ["move-window", "-d", "-s", "$0:0", "-t", "$0:5"],
                    "pane" => ["split-window", "-d", "-t", "%0"],
                    _ => throw new ArgumentException("Unknown mutation.", nameof(change)),
                };
                Assert.Equal(0, (await raw.ExecuteAsync(mutation, cancellationToken)).ExitCode);
            }
        }).ConnectAsync(token);

        Exception? failure = await Record.ExceptionAsync(() => server.CaptureSnapshotAsync(SnapshotDepth.Panes, token));

        Assert.True(changed);
        InconsistentSnapshotException inconsistent = Assert.IsType<InconsistentSnapshotException>(failure);
        Assert.Equal(SnapshotDepth.Panes, inconsistent.RequestedDepth);
        Assert.Equal(server.Generation, inconsistent.Generation);
        Assert.Equal(TmuxDispatchState.Dispatched, inconsistent.Dispatch);
        Assert.False(server.Sessions.IsCaptured);
    }

    [UnixFact]
    public async Task Scalar_changes_between_reads_do_not_invalidate_membership()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:5"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-t", "%0"], token)).ExitCode);
        bool renamed = false;
        bool selected = false;
        Server server = await CreateInterceptedServer(raw, async (startInfo, cancellationToken) =>
        {
            if (!renamed && startInfo.ArgumentList.Contains("list-windows"))
            {
                renamed = true;
                Assert.Equal(0, (await raw.ExecuteAsync(["rename-session", "-t", "$0", "renamed"], cancellationToken)).ExitCode);
                Assert.Equal(0, (await raw.ExecuteAsync(["select-window", "-t", "$0:5"], cancellationToken)).ExitCode);
            }
            if (!selected && startInfo.ArgumentList.Contains("list-panes"))
            {
                selected = true;
                Assert.Equal(0, (await raw.ExecuteAsync(["rename-window", "-t", "$0:0", "renamed"], cancellationToken)).ExitCode);
                Assert.Equal(0, (await raw.ExecuteAsync(["select-pane", "-t", "%2"], cancellationToken)).ExitCode);
                Assert.Equal(0, (await raw.ExecuteAsync(["resize-window", "-t", "$0:0", "-x", "100", "-y", "40"], cancellationToken)).ExitCode);
            }
        }).ConnectAsync(token);

        Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);

        Assert.True(renamed && selected);
        Assert.Single(captured.Sessions);
        Assert.Equal(2, captured.Windows.Count);
        Assert.Equal(3, captured.Panes.Count);
    }

    [UnixFact]
    public async Task Every_capture_depth_rejects_a_replacement_daemon()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await CreateInterceptedServer(raw, (_, _) => ValueTask.CompletedTask).ConnectAsync(token);
        ServerGeneration expected = Assert.IsType<ServerGeneration>(server.Generation);
        using Process daemon = Process.GetProcessById(expected.ProcessId);
        Task exited = daemon.WaitForExitAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        await exited;
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", raw.SessionName], token)).ExitCode);
        Server replacement = await CreateInterceptedServer(raw, (_, _) => ValueTask.CompletedTask).ConnectAsync(token);
        Assert.NotEqual(expected, replacement.Generation);

        foreach (SnapshotDepth depth in Enum.GetValues<SnapshotDepth>())
        {
            StaleServerGenerationException failure = await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => server.CaptureSnapshotAsync(depth, token));
            Assert.Equal(expected, failure.Expected);
            Assert.Equal(replacement.Generation, failure.Actual);
        }
    }

    private static Server CreateInterceptedServer(
        RawTmuxTestContext raw,
        Func<ProcessStartInfo, CancellationToken, ValueTask> beforeStart)
    {
        var transport = new TmuxProcessTransport(
            raw.TmuxBinaryPath,
            ["-f", "/dev/null", "-S", raw.SocketPath],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("tmux did not start.");
            },
            beforeStart: beforeStart);
        var options = new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null"
        };
        return new Server(new TmuxConnection(options, transport.ExecuteAsync), null, null);
    }
}
