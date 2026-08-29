using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeSessionTests
{
    [UnixFact]
    public async Task A_control_client_answers_commands_and_reports_what_it_saw()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        Assert.True(control.IsRunning);

        // Attaching answers itself before anyone asks. A reader that handed
        // that block to the first caller would answer every command with the
        // previous one's output, so the first command has to get its own.
        IReadOnlyList<string> panes = await control.SendAsync(
            TmuxCommand.Create("list-panes", "-F", "#{pane_id}"),
            token);

        Assert.Equal(["%0"], panes);

        IReadOnlyList<string> sessions = await control.SendAsync(
            TmuxCommand.Create("list-sessions"),
            token);
        Assert.Single(sessions);
    }

    [UnixFact]
    public async Task A_pane_id_inside_a_block_is_data_rather_than_a_notification()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        // tmux marks notifications with a leading percent, and a pane id starts
        // with one too. Ending the block at the first such line would truncate
        // this answer and leave the rest to be read as notifications.
        IReadOnlyList<string> reported = await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "#{pane_id}"),
            token);

        Assert.Equal(["%0"], reported);

        // The stream is still in step: a command after the ambiguous one is
        // answered with its own output rather than the leftovers.
        Assert.Equal(
            ["ok"],
            await control.SendAsync(TmuxCommand.Create("display-message", "-p", "ok"), token));
    }

    [UnixFact]
    public async Task A_guard_looking_line_inside_a_block_is_data()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        IReadOnlyList<string> reported = await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "%%end 9 9 1"),
            token);

        Assert.Equal(["%end 9 9 1"], reported);
        Assert.Equal(
            ["ok"],
            await control.SendAsync(TmuxCommand.Create("display-message", "-p", "ok"), token));
    }

    [UnixFact]
    public async Task A_failing_command_faults_only_its_own_caller()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        await Assert.ThrowsAsync<ControlModeCommandException>(
            () => control.SendAsync(TmuxCommand.Create("no-such-tmux-command"), token));

        // The client survives a rejected command, so the session is still
        // usable rather than needing to be torn down and reopened.
        Assert.True(control.IsRunning);
        Assert.Equal(["still-here"], await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "still-here"),
            token));
    }

    [UnixFact]
    public async Task A_command_alias_cannot_move_a_reply_to_the_next_caller()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RawTmuxResult configured = await raw.ExecuteAsync(
            [
                "set-option",
                "-s",
                "command-alias[200]",
                "libtmux-expand=display-message -p one; display-message -p two",
            ],
            token);
        Assert.Equal(0, configured.ExitCode);
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        Task<IReadOnlyList<string>> expanded = control.SendAsync(
            TmuxCommand.Create("libtmux-expand"),
            token);
        Task<IReadOnlyList<string>> following = control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "following"),
            token);

        Assert.Equal(["one", "two"], await expanded);
        Assert.Equal(["following"], await following);
    }

    [UnixFact]
    public async Task Typed_arguments_are_literal_tmux_arguments()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);
        const string Value = "space ' ; $HOME \\ π";

        IReadOnlyList<string> output = await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", Value),
            token);

        Assert.Equal([Value], output);
    }

    [UnixFact]
    public async Task Hook_blocks_do_not_replace_command_output()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RawTmuxResult configured = await raw.ExecuteAsync(
            [
                "set-hook",
                "-g",
                "after-list-panes",
                "display-message -p hook-output",
            ],
            token);
        Assert.Equal(0, configured.ExitCode);
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        IReadOnlyList<string> panes = await control.SendAsync(
            TmuxCommand.Create("list-panes", "-F", "#{pane_id}"),
            token);

        Assert.Equal(["%0"], panes);
        Assert.Equal(
            ["aligned"],
            await control.SendAsync(
                TmuxCommand.Create("display-message", "-p", "aligned"),
                token));
    }

    [UnixFact]
    public async Task Cancellation_keeps_later_callers_behind_the_request_fence()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);
        string channel = $"control-{Guid.NewGuid():N}";
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<IReadOnlyList<string>> blocked = control.SendAsync(
            TmuxCommand.Create("wait-for", channel),
            callerCancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);

        Task<IReadOnlyList<string>> following = control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "after-wait"),
            token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        Assert.False(following.IsCompleted);

        RawTmuxResult signal = await raw.ExecuteAsync(["wait-for", "-S", channel], token);
        Assert.Equal(0, signal.ExitCode);
        Assert.Equal(["after-wait"], await following);
    }

    [UnixFact]
    public async Task Pane_output_arrives_decoded()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);

        await control.SendAsync(
            TmuxCommand.Create(
                "send-keys",
                "-t",
                "%0",
                "echo libtmux-control-marker",
                "Enter"),
            token);

        // tmux escapes the payload the way it escapes an option value, so a
        // reader that passed it through would report the literal escape
        // sequence "\015" where the program wrote a carriage return.
        string seen = string.Empty;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await foreach (TmuxEvent observed in control.Events.WithCancellation(timeout.Token))
        {
            if (observed is TmuxOutputEvent output)
            {
                seen += output.Data;
                if (seen.Contains("libtmux-control-marker", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        Assert.Contains("libtmux-control-marker", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("\\015", seen, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task The_event_stream_ends_with_an_exit()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);

        await control.SendAsync(TmuxCommand.Create("kill-server"), token);

        List<TmuxEvent> observed = [];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await foreach (TmuxEvent item in control.Events.WithCancellation(timeout.Token))
        {
            observed.Add(item);
        }

        // The stream completes rather than hanging, and says why it stopped, so
        // a caller awaiting it is released instead of waiting for a client that
        // is gone.
        Assert.NotEmpty(observed);
        Assert.IsType<TmuxExitEvent>(observed[^1]);
        await control.DisposeAsync();
    }

    [UnixFact]
    public async Task Startup_rejects_a_server_restart_between_discovery_and_attach()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server original = await ConnectAsync(raw, token);
        ServerGeneration expected = original.Generation
            ?? throw new InvalidOperationException("The test server was not materialized.");
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-control-generation-{Guid.NewGuid():N}");
        string wrapper = Path.Combine(directory, "tmux-wrapper");
        string forgedGenerationAlias =
            $"display-message=display-message -p {expected.ProcessId}:{expected.StartTime} ; send-keys -l --";
        Directory.CreateDirectory(directory);
        Task<IControlModeSession>? startup = null;

        try
        {
            string script = $$"""
                #!/bin/sh
                set -eu
                generation_probe=0
                for argument in "$@"; do
                    if [ "$argument" = '#{pid}:#{start_time}' ]; then
                        generation_probe=1
                    fi
                done
                if [ "$generation_probe" = 1 ]; then
                    {{ShellQuote(raw.TmuxBinaryPath)}} "$@"
                    {{ShellQuote(raw.TmuxBinaryPath)}} \
                        -S {{ShellQuote(raw.SocketPath)}} \
                        kill-server
                    {{ShellQuote(raw.TmuxBinaryPath)}} \
                        -S {{ShellQuote(raw.SocketPath)}} \
                        -f /dev/null \
                        new-session -d -s successor
                    {{ShellQuote(raw.TmuxBinaryPath)}} \
                        -S {{ShellQuote(raw.SocketPath)}} \
                        set-option -s 'command-alias[200]' \
                        {{ShellQuote(forgedGenerationAlias)}}
                    exit 0
                fi
                exec {{ShellQuote(raw.TmuxBinaryPath)}} "$@"
                """;
            await File.WriteAllTextAsync(wrapper, script, token);
            File.SetUnixFileMode(
                wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await WaitUntilAsync(() => CanExecute(wrapper), token);

            Server server = Server.Open(new ServerConnectionOptions(
                tmuxBinaryPath: wrapper,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"));
            startup = server.EnterControlModeAsync(cancellationToken: token);

            StaleServerGenerationException error =
                await Assert.ThrowsAsync<StaleServerGenerationException>(async () => await startup);
            Assert.Equal(expected, error.Expected);
            Assert.Null(error.Actual);
        }
        finally
        {
            if (startup?.IsCompletedSuccessfully == true)
            {
                await startup.Result.DisposeAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task A_canceled_attach_is_disposed_before_the_call_returns()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-control-start-{Guid.NewGuid():N}");
        string wrapper = Path.Combine(directory, "tmux-wrapper");
        string pidFile = Path.Combine(directory, "client.pid");
        Directory.CreateDirectory(directory);
        int clientPid = 0;

        try
        {
            string script = $"""
                #!/bin/sh
                for argument in "$@"; do
                    if [ "$argument" = "-C" ]; then
                        echo "$$" > {ShellQuote(pidFile)}
                        IFS= read -r ignored
                        exit 0
                    fi
                done
                exec {ShellQuote(raw.TmuxBinaryPath)} "$@"
                """;
            await File.WriteAllTextAsync(
                wrapper,
                script,
                TestContext.Current.CancellationToken);
            File.SetUnixFileMode(
                wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Linux refuses to exec a file while any process holds a write
            // descriptor for it. Process.Start forks, and the child keeps every
            // inherited descriptor until it execs, so a sibling test starting a
            // process while this wrapper is being written makes the first exec
            // fail with ETXTBSY though the wrapper itself is correct. That
            // descriptor goes when the child execs, so wait for the wrapper to
            // run rather than racing it.
            await WaitUntilAsync(
                () => CanExecute(wrapper),
                TestContext.Current.CancellationToken);

            Server server = await Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: wrapper,
                    socketPath: raw.SocketPath,
                    configurationFile: "/dev/null"),
                TestContext.Current.CancellationToken);
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            Task<IControlModeSession> startup = server.EnterControlModeAsync(
                cancellationToken: startupCancellation.Token);

            await WaitUntilAsync(
                () => TryReadProcessId(pidFile, out clientPid),
                TestContext.Current.CancellationToken);
            startupCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await startup);
            await WaitUntilAsync(
                () => !IsProcessAlive(clientPid),
                TestContext.Current.CancellationToken);
            Assert.False(IsProcessAlive(clientPid));
        }
        finally
        {
            if (IsProcessAlive(clientPid))
            {
                using Process process = Process.GetProcessById(clientPid);
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task Startup_drains_standard_error_before_waiting_for_attach()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-control-stderr-{Guid.NewGuid():N}");
        string wrapper = Path.Combine(directory, "tmux-wrapper");
        Directory.CreateDirectory(directory);

        try
        {
            string script = $"""
                #!/bin/sh
                for argument in "$@"; do
                    if [ "$argument" = "-C" ]; then
                        dd if=/dev/zero bs=65536 count=4 1>&2 2>/dev/null
                        exec {ShellQuote(raw.TmuxBinaryPath)} "$@"
                    fi
                done
                exec {ShellQuote(raw.TmuxBinaryPath)} "$@"
                """;
            await File.WriteAllTextAsync(
                wrapper,
                script,
                TestContext.Current.CancellationToken);
            File.SetUnixFileMode(
                wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await WaitUntilAsync(
                () => CanExecute(wrapper),
                TestContext.Current.CancellationToken);

            Server server = await Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: wrapper,
                    socketPath: raw.SocketPath,
                    configurationFile: "/dev/null"),
                TestContext.Current.CancellationToken);
            using var startupBudget = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            startupBudget.CancelAfter(TimeSpan.FromSeconds(3));

            await using IControlModeSession control = await server.EnterControlModeAsync(
                cancellationToken: startupBudget.Token);
            Assert.True(control.IsRunning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);

    // errno 26. Process.Start surfaces it as the native error code on Linux.
    private const int TextFileBusy = 26;

    private static bool CanExecute(string path)
    {
        try
        {
            using Process? probe = Process.Start(
                new ProcessStartInfo(path, "-V")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
            probe?.WaitForExit();
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == TextFileBusy)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadProcessId(string path, out int processId)
    {
        processId = 0;
        try
        {
            return int.TryParse(
                File.ReadAllText(path),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out processId);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }
}
