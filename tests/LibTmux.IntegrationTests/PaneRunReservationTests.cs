using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class PaneRunReservationTests
{
    [UnixFact]
    public async Task Public_pane_run_reports_status_retains_timeout_and_borrows_server()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string binary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        var options = new TmuxTestOptions(new ServerConnectionOptions
        {
            TmuxBinaryPath = binary,
            SocketName = $"libtmux-pane-command-{Guid.NewGuid():N}",
        });
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            cancellationToken);

        PaneRunResult success = await scope.Pane.RunAsync(
            "printf 'completed\\n'",
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        Assert.Equal(scope.Pane.Id, success.PaneId);
        Assert.Equal(0, success.ExitStatus);
        Assert.False(success.TimedOut);

        PaneRunResult failure = await scope.Pane.RunAsync(
            "exit 7",
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        Assert.Equal(7, failure.ExitStatus);
        Assert.False(failure.TimedOut);

        Assert.Contains(
            await scope.Server.GetPanesAsync(cancellationToken),
            pane => pane.Id == scope.Pane.Id);

        string gate = $"libtmux-pane-command-{Guid.NewGuid():N}";
        string quotedBinary = "'" + binary.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        try
        {
            PaneRunResult timedOut = await scope.Pane.RunAsync(
                $"{quotedBinary} wait-for {gate}",
                TimeSpan.FromMilliseconds(20),
                cancellationToken: cancellationToken);
            Assert.True(timedOut.TimedOut);
            Assert.Null(timedOut.ExitStatus);

            LibTmuxException pending = await Assert.ThrowsAsync<LibTmuxException>(() =>
                scope.Pane.RunAsync(
                    "printf 'would retry\\n'",
                    TimeSpan.FromSeconds(1),
                    cancellationToken: cancellationToken));
            Assert.Contains("already has a command running", pending.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                await scope.Server.GetPanesAsync(cancellationToken),
                pane => pane.Id == scope.Pane.Id);
        }
        finally
        {
            await scope.Server.WaitForAsync(
                new WaitForRequest(gate, TmuxWaitMode.Signal),
                CancellationToken.None);
        }
    }
}
