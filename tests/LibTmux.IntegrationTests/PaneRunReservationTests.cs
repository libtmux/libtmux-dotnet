using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.Engineering;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class PaneRunReservationTests
{
    [UnixFact]
    public async Task Public_pane_run_awaits_pre_exec_shell_and_refuses_drawn_tmux_pane()
    {
        TmuxFeatureSupport.RequireNamedBuffers();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(
            WorkspaceSocketRoot.Root,
            $"preexec-{Guid.NewGuid():N}"[..16]);
        Directory.CreateDirectory(directory);
        string symlink = Path.Combine(directory, "tmux");
        File.CreateSymbolicLink(symlink, "/bin/sh");
        string release = Path.Combine(directory, "release");
        string shim = Path.Combine(directory, "login-shim");
        await File.WriteAllTextAsync(
            shim,
            "#!/bin/bash\nexec -a locale /bin/bash --noprofile --norc -c "
                + PaneRunner.ShellQuote(
                    $"while [ ! -e {PaneRunner.ShellQuote(release)} ]; do sleep 0.01; done; "
                    + "exec env PS1= ENV= /bin/sh") + "\n",
            cancellationToken);
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var options = new TmuxTestOptions(new ServerConnectionOptions
            {
                TmuxBinaryPath = RawTmuxTestContext.ConfiguredBinary,
                SocketName = $"lt-preexec-{Guid.NewGuid():N}"[..20],
                ChildEnvironment = new Dictionary<string, string?> { ["BASH_ENV"] = null },
            });
            TmuxTestFactory factory = new();
            await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
                options,
                cancellationToken);

            await scope.Session.Options.SetAsync(new("default-shell", shim), cancellationToken);
            await scope.Session.Options.SetAsync(new("default-command", string.Empty), cancellationToken);
            TmuxCommandResult split = await scope.Server.ExecuteCommandAsync(
                ["split-window", "-d", "-P", "-F", "#{pane_id}", "-t", scope.Pane.Id.ToString()],
                cancellationToken);
            PaneId id = PaneId.Parse(Assert.Single(split.StandardOutputLines));
            Pane preExec = await scope.Server.GetPaneAsync(id, cancellationToken);
            Stopwatch observed = Stopwatch.StartNew();
            while (preExec.CurrentCommand != "locale" && observed.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(5, cancellationToken);
                preExec = await scope.Server.GetPaneAsync(id, cancellationToken);
            }

            Assert.Equal("locale", preExec.CurrentCommand);
            Assert.Null(preExec.RawFormatFields["pane_start_command"]);
            Assert.Equal("0", preExec.RawFormatFields["cursor_x"]);
            Assert.Equal("0", preExec.RawFormatFields["cursor_y"]);
            Assert.Equal("0", preExec.RawFormatFields["history_size"]);
            Task<PaneRunResult> run = preExec.RunAsync(
                "printf 'ready\\n'", TimeSpan.FromSeconds(5), cancellationToken);
            await Task.Delay(80, cancellationToken);
            await File.WriteAllTextAsync(release, string.Empty, cancellationToken);
            PaneRunResult ran = await run;
            Assert.Equal(0, ran.ExitStatus);
            Assert.Equal(["ready"], ran.Output);

            TmuxCommandResult drawnSplit = await scope.Server.ExecuteCommandAsync(
                [
                    "split-window",
                    "-d",
                    "-P",
                    "-F",
                    "#{pane_id}",
                    "-t",
                    scope.Pane.Id.ToString(),
                    symlink,
                    "-c",
                    "printf 'nested\\n'; read _ || true",
                ],
                cancellationToken);
            Pane drawn = await scope.Server.GetPaneAsync(
                PaneId.Parse(Assert.Single(drawnSplit.StandardOutputLines)),
                cancellationToken);
            PaneWaitResult printed = await drawn.WaitUntilAsync(
                rows => rows.Any(row => row.Contains("nested", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            Assert.True(printed.Found);

            TmuxPaneException refused = await Assert.ThrowsAsync<TmuxPaneException>(() =>
                drawn.RunAsync(
                    "printf 'never\\n'",
                    TimeSpan.FromSeconds(5),
                    cancellationToken));
            Assert.Equal(TmuxDispatchState.NotDispatched, refused.Dispatch);
            Assert.Contains(
                "is running 'tmux', not a POSIX-compatible shell.",
                refused.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task Public_pane_run_reports_status_retains_timeout_and_borrows_server()
    {
        TmuxFeatureSupport.RequireNamedBuffers();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string binary = RawTmuxTestContext.ConfiguredBinary;
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
