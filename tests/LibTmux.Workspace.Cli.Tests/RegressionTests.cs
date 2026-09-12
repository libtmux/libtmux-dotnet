using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace LibTmux.Workspace.Cli.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class RegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-regression-" + Guid.NewGuid().ToString("N"));
    public RegressionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Progress_templates_distinguish_ordinals_from_completed_work()
    {
        WorkspacePlan plan = WorkspacePlan.Parse(DocumentStore.Parse("session_name: '[red]literal[/]'\nwindows: [{window_name: editor, window_index: 9, panes: [null, null]}, {panes: [null, null]}]"), Path.Combine(_root, "progress.yaml"), new DocumentStore(Context(TextWriter.Null)));
        using ProgressDisplay display = new(new ProgressOptions("{{session}} {session} {window_index}/{window_total} {pane_index}/{pane_total} {pane_done} {session_pane_progress} {overall_percent} {unknown} {session!r} {overall_percent:03d}", 3), 80, 10, false);
        display.StartWorkspace(plan);
        display.StartWindow(plan.Windows[0], 1);
        display.StartPane(2);
        Assert.Contains(" 0 0/4 0 ", display.Format(), StringComparison.Ordinal);
        display.CompletePane();
        Assert.Equal("{session} [red]literal[/] 1/2 2/2 1 1/4 25 {unknown} {session!r} {overall_percent:03d}", display.Format());
        Dictionary<string, string> presets = new()
        {
            ["default"] = "Loading workspace: [red]literal[/] ██░░░░░░░░ 1/2 win · 1/2 pane editor",
            ["minimal"] = "Loading workspace: [red]literal[/] [1/2]",
            ["window"] = "Loading workspace: [red]literal[/] ░░░░░░░░░░ 0/2",
            ["pane"] = "Loading workspace: [red]literal[/] ██░░░░░░░░ 1/4",
            ["verbose"] = "Loading workspace: [red]literal[/] [window 1 of 2 · pane 1 of 2] editor"
        };
        foreach (var preset in presets)
        {
            using ProgressDisplay named = new(new ProgressOptions(preset.Key, 0), 80, 10, false);
            named.StartWorkspace(plan);
            named.StartWindow(plan.Windows[0], 1);
            named.StartPane(1);
            named.CompletePane();
            Assert.Equal(preset.Value, named.Format());
        }
        using ProgressDisplay totals = new(new ProgressOptions("{workspace_path}|{session}|{window}|{window_index}|{window_total}|{window_progress}|{pane_index}|{pane_total}|{pane_progress}|{progress}|{windows_done}|{windows_remaining}|{window_progress_rel}|{pane_done}|{pane_remaining}|{pane_progress_rel}|{session_pane_total}|{session_panes_done}|{session_panes_remaining}|{session_pane_progress}|{overall_percent}|{summary}|{bar}|{pane_bar}|{window_bar}|{status_icon}", 0), 80, 10, false);
        totals.StartWorkspace(plan);
        totals.StartWindow(plan.Windows[0], 1);
        totals.StartPane(2);
        totals.CompletePane();
        Assert.Equal(plan.Source + "|[red]literal[/]|editor|1|2|1/2|2|2|2/2|1/2 win · 2/2 pane|0|2|0/2|1|1|1/2|4|1|3|1/4|25|[0 win, 1 panes]|██░░░░░░░░|██░░░░░░░░|░░░░░░░░░░|", totals.Format());
    }

    [Fact]
    public void Progress_panels_preserve_interleaved_fragments_and_reset_delimiters()
    {
        WorkspacePlan plan = WorkspacePlan.Parse(DocumentStore.Parse("session_name: panel\nwindows: [{panes: [null]}]"), Path.Combine(_root, "progress.yaml"), new DocumentStore(Context(TextWriter.Null)));
        using ProgressDisplay display = new(new ProgressOptions("HEADER", -1), 80, 8, false);
        display.StartWorkspace(plan);
        display.Script("stderr", "err");
        display.Script("stdout", "out");
        Assert.Equal("HEADER\r\nerr\r\nout\r\n", display.Render());
        display.Script("stdout", "\r");
        display.Script("stderr", "\r");
        display.Script("stdout", "");
        display.Script("stdout", "\nnext\n");
        display.Script("stderr", "\n");
        Assert.Equal("HEADER\r\nout\r\nerr\r\nnext\r\n", display.Render());
        display.Script("stdout", "old\r");
        display.StartWorkspace(plan);
        display.Script("stdout", "\nnew\n");
        Assert.Equal("HEADER\r\n\r\nnew\r\n", display.Render());
        display.StartWorkspace(plan);
        display.Script("stdout", string.Concat(Enumerable.Repeat("e\u0301🙂", 3000)));
        string line = display.Render().Split("\r\n")[1];
        Assert.True(line.StartsWith("e\u0301", StringComparison.Ordinal) || line.StartsWith("🙂", StringComparison.Ordinal));
        Assert.DoesNotContain('\ufffd', line);
        Assert.True(line.GetCellWidth() <= 79);
    }

    [Fact]
    public void Progress_environment_is_only_validated_for_active_display()
    {
        CommandLine graph = new();
        Dictionary<string, string?> environment = new() { [ProgressOptions.FormatEnvironment] = "from-env", [ProgressOptions.LinesEnvironment] = "invalid" };
        Invocation load = graph.Parse(["load", "workspace", "-d"]);
        Assert.Throws<CliException>(() => ProgressOptions.Resolve(load, environment, true));
        Assert.Null(ProgressOptions.Resolve(load, environment, false));
        Assert.Null(ProgressOptions.Resolve(graph.Parse(["load", "workspace", "-d", "--json"]), environment, true));
        Assert.Null(ProgressOptions.Resolve(graph.Parse(["load", "workspace", "-d", "--no-progress"]), environment, true));
        Invocation selected = graph.Parse(["load", "workspace", "-d", "--progress-lines", "0", "--progress-format", "selected"]);
        Assert.Equal(new ProgressOptions("selected", 0), ProgressOptions.Resolve(selected, environment, true));
        environment[ProgressOptions.LinesEnvironment] = "-1";
        Assert.Equal(new ProgressOptions("from-env", -1), ProgressOptions.Resolve(load, environment, true));
        environment[ProgressOptions.EnabledEnvironment] = "0";
        Assert.Null(ProgressOptions.Resolve(selected, environment, true));
        Assert.NotEmpty(graph.Parse(["load", "workspace", "-d", "--no-progress", "--progress-lines", "-2"]).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress_cleanup_failure_does_not_skip_the_primary_diagnostic(bool inaccessible)
    {
        using ClearFailureWriter error = new(inaccessible);
        using StringWriter log = new();
        CliContext context = Context(TextWriter.Null) with { Error = error };
        Invocation invocation = new CommandLine().Parse(["load", "workspace", "-d"]);
        ProgressDisplay progress = new(new ProgressOptions("START", 0), 80, 10, false);
        await using Output output = new(context, invocation, log, progress);
        await output.ProgressAsync(display => display.StartBridge(), force: true);
        await output.DiagnosticAsync("primary-failure", "Primary operation failed.");
        Assert.Equal(1, error.Failures);
        Assert.Contains("Primary operation failed.", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("primary-failure", JsonNode.Parse(log.ToString())!["code"]!.ToString());
    }

    [Fact]
    public void Normalization_carries_command_settings_and_defaults_to_suppressed_history()
    {
        JsonObject document = DocumentStore.Parse("session_name: state\nwindows: [{panes: [{shell_command: [{cmd: first, enter: false, sleep_after: 1}, second, {cmd: third, sleep_after: 0}, fourth]}]}]");
        WorkspacePlan plan = WorkspacePlan.Parse(document, Path.Combine(_root, "workspace.yaml"), new DocumentStore(Context(TextWriter.Null)));
        CommandPlan[] commands = plan.Windows[0].Panes[0].Commands;
        Assert.Equal(" first", commands[0].Text);
        Assert.False(commands[1].Enter);
        Assert.Equal(1, commands[1].After);
        Assert.Equal(0, commands[3].After);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("fifo")]
    [InlineData("device")]
    [InlineData("symlink")]
    public async Task Log_destination_failure_precedes_backend_work(string kind)
    {
        string file = Path.Combine(_root, "workspace.yaml");
        string marker = Path.Combine(_root, "backend-called");
        string backend = Path.Combine(_root, "backend");
        string destination = kind == "directory" ? _root : kind == "device" ? "/dev/null" : Path.Combine(_root, kind);
        await File.WriteAllTextAsync(file, "session_name: logging\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(backend, "#!/bin/sh\nprintf called > '" + marker + "'\nexit 1\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(backend, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (kind == "fifo")
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start("mkfifo", destination)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        if (kind == "symlink") File.CreateSymbolicLink(destination, file);
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["LIBTMUX_TMUX"] = backend };
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync(["load", file, "-d", "--log-file", destination, "--json"], output, error, _root, environment, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker));
        Assert.Equal(1, code);
        Assert.Empty(output.ToString());
        Assert.Equal("log-file-unavailable", JsonNode.Parse(error.ToString())!["code"]!.ToString());
        Assert.Equal("session_name: logging\nwindows: [{panes: [null]}]", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("./working")]
    [InlineData("absolute")]
    public async Task Before_script_uses_direct_argv_session_directory_and_created_session(string? start)
    {
        string socket = Path.Combine(_root, "script.socket");
        string configDirectory = Path.Combine(_root, "config");
        string directory = Path.Combine(configDirectory, "working");
        Directory.CreateDirectory(directory);
        string expectedDirectory = start is null ? _root : directory;
        string file = Path.Combine(configDirectory, "script.json");
        JsonObject document = new()
        {
            ["session_name"] = "script",
            ["before_script"] = "/bin/sh -c 'test -S \"" + socket + "\" && test \"$(pwd)\" = \"" + expectedDirectory + "\" && printf ready'",
            ["windows"] = new JsonArray(new JsonObject { ["panes"] = new JsonArray((JsonNode?)null) }),
        };
        if (start is not null) document["start_directory"] = start == "absolute" ? directory : start;
        await File.WriteAllTextAsync(file, document.ToJsonString(), TestContext.Current.CancellationToken);
        string log = Path.Combine(_root, "operation.ndjson");
        if (start is not null)
        {
            await File.WriteAllTextAsync(log, "{\"preserved\":true}\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            var result = await Run("--log-level", "debug", "load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson", "--log-file", "operation.ndjson");
            Assert.True(result.Code == 0, result.Error + result.Output);
            JsonNode[] events = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.True(Array.FindIndex(events, item => item["event"]!.ToString() == "session-created") < Array.FindIndex(events, item => item["event"]!.ToString() == "script-output"));
            Assert.Single(events, item => item["event"]!.ToString() == "completed");
            string sessionId = events.Single(item => item["event"]!.ToString() == "session-created")["data"]!["session_id"]!.ToString();
            TmuxCommandResult pane = await server.ExecuteCommandAsync(["display-message", "-p", "-t", sessionId + ":", "#{pane_current_path}"], TestContext.Current.CancellationToken);
            Assert.Equal(0, pane.ExitCode);
            Assert.Equal(expectedDirectory, System.Text.Encoding.UTF8.GetString(pane.StandardOutput.Span).TrimEnd('\n'));
            Assert.True(File.Exists(log));
            JsonNode[] logged = (await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken)).Select(line => JsonNode.Parse(line)!).ToArray();
            if (start is not null) { Assert.True(logged[0]["preserved"]!.GetValue<bool>()); logged = logged[1..]; }
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | (start is null ? 0 : UnixFileMode.GroupRead), File.GetUnixFileMode(log));
            Assert.Equal(events.Select(item => item["event"]!.ToString()), logged.Select(item => item["event"]!.ToString()));
            Assert.Equal("debug", logged.Single(item => item["event"]!.ToString() == "script-output")["severity"]!.ToString());
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Python_shell_bridge_executes_against_the_explicit_socket()
    {
        string socket = Path.Combine(_root, "python.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            await server.ExecuteCommandAsync(["new-session", "-d", "-s", "bridge"], TestContext.Current.CancellationToken);
            string[] arguments = ["shell", "bridge", "-S", socket, "--code", "--no-startup", "-c", "import sys; print(session.session_name); sys.stderr.write('warning-tail')"];
            var result = await Run([.. arguments, "--json"]);
            Assert.Equal(0, result.Code);
            JsonNode captured = JsonNode.Parse(result.Output)!;
            Assert.Contains("bridge\n", captured["stdout"]!.ToString(), StringComparison.Ordinal);
            Assert.Equal("warning-tail", captured["stderr"]!.ToString());
            var human = await Run(arguments);
            Assert.Equal(0, human.Code);
            Assert.Equal(captured["stdout"]!.ToString(), human.Output);
            Assert.Equal("warning-tail", human.Error);
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Python_bridge_leaves_native_output_options_with_the_cli(bool equals, bool machine)
    {
        string file = Path.Combine(_root, "extension.yaml");
        string python = Path.Combine(_root, "python");
        string trace = Path.Combine(_root, "arguments");
        string childEnvironment = Path.Combine(_root, "child-environment");
        string log = Path.Combine(_root, "bridge.ndjson");
        await File.WriteAllTextAsync(file, "session_name: bridge\nplugins: [example]\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(python, $$"""
            #!/bin/sh
            if test "$1" = -c; then printf '1.74.0\n'; exit 0; fi
            printf '%s\n' "$@" > '{{trace}}'
            printf '%s' "$TMUXP_PROGRESS" > '{{childEnvironment}}'
            printf 'bridge \342\230\203\033[31m\n'
            printf 'warning\rline\n' >&2
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] level = equals ? ["--log-level=debug"] : ["--log-level", "debug"];
        string[] destination = equals ? ["--log-file=bridge.ndjson"] : ["--log-file", "bridge.ndjson"];
        string[] progress = equals ? ["--progress-format=window", "--progress-lines=-1"] : ["--progress-format", "window", "--progress-lines", "-1"];
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_WORKSPACE_PYTHON"] = python, ["TMUXP_PROGRESS"] = "1" };
        using StringWriter output = new();
        using StringWriter error = new();
        string[] mode = machine ? ["--json"] : [];
        int code = await CliRunner.RunAsync([.. level, "load", "-d", .. destination, .. progress, "--no-progress", .. mode, "--", file], output, error, _root, environment, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        if (machine)
        {
            Assert.Empty(error.ToString());
            Assert.Equal("ok", JsonNode.Parse(output.ToString())!["status"]!.ToString());
        }
        else
        {
            Assert.Equal("bridge \u2603\u001b[31m\nLoaded Python workspace extensions.\n", output.ToString());
            Assert.Equal("warning\rline\n", error.ToString());
        }
        Assert.Equal(["load", "-d", "--", file], (await File.ReadAllLinesAsync(trace, TestContext.Current.CancellationToken))[3..]);
        Assert.Equal("0", await File.ReadAllTextAsync(childEnvironment, TestContext.Current.CancellationToken));
        Assert.Equal("1", environment["TMUXP_PROGRESS"]);
        string text = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\u001b', text);
        JsonNode[] records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal("started", records[0]["event"]!.ToString());
        Assert.Equal("completed", records[^1]["event"]!.ToString());
        Assert.Contains(records, item => item["event"]!.ToString() == "script-output" && item["data"]!["stream"]!.ToString() == "stdout" && item["data"]!["text"]!.ToString().Contains('\u2603'));
        Assert.Contains(records, item => item["event"]!.ToString() == "script-output" && item["data"]!["stream"]!.ToString() == "stderr");
    }

    [Fact]
    public async Task Child_pipe_failure_finishes_without_waiting_for_full_child_output()
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        CliContext context = Context(new BrokenWriter()) with { CancellationToken = limit.Token };
        Output output = new(context, new CommandLine().Parse(["shell", "-c", "print()", "--ndjson"]));
        Exception? error = await Record.ExceptionAsync(() => ProcessCommands.RunProcessAsync(context, output, "/bin/sh", ["-c", "while :; do printf 'long-output-line\\n'; done"], _root, true));
        Assert.IsType<IOException>(error);
        Assert.False(limit.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_output_waits_for_async_writes_and_cancels_them(bool fileSink)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using BlockingWriter blocked = new();
        CliContext context = Context(fileSink ? TextWriter.Null : blocked) with { CancellationToken = cancellation.Token };
        await using Output output = new(context, new CommandLine().Parse(["--log-level", "debug", "shell", "-c", "print()", "--ndjson"]), fileSink ? blocked : null);
        Task<ChildResult> child = ProcessCommands.RunProcessAsync(context, output, "/bin/sh", ["-c", "while :; do printf 'output\\n'; done"], _root, true);
        try
        {
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(child.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellation.Cancel();
            blocked.Release.TrySetResult();
            try { await child; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("success", true)]
    [InlineData("script", true)]
    [InlineData("cancel", true)]
    public async Task Late_log_failure_preserves_the_workspace_result(string outcome, bool brokenError)
    {
        string socket = Path.Combine(_root, "logging.socket");
        string file = Path.Combine(_root, "logging.yaml");
        string script = outcome == "script" ? "/bin/false" : outcome == "cancel" ? "/bin/sh -c 'printf ready; sleep 60'" : "/bin/echo ready";
        await File.WriteAllTextAsync(file, "session_name: logging\nbefore_script: " + script + "\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using CancellingWriter stdout = new(outcome == "cancel" ? cancellation : null);
        using StringWriter stderr = brokenError ? new BrokenWriter() : new StringWriter();
        CliContext context = Context(stdout) with { Error = stderr, CancellationToken = cancellation.Token };
        Invocation invocation = new CommandLine().Parse(["--log-level", "debug", "load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson"]);
        await using Output output = new(context, invocation, new FailingLogWriter(outcome == "success" ? "script-output" : "failed"));
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            int code = await new ExecutionCommands(context, invocation, output).LoadAsync();
            Assert.Equal(outcome == "success" ? 0 : outcome == "cancel" ? 130 : 1, code);
            JsonNode[] events = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            JsonNode terminal = Assert.Single(events, item => item["event"]!.ToString() is "completed" or "failed");
            Assert.Equal(outcome == "success" ? "ok" : "error", terminal["data"]!["status"]!.ToString());
            Assert.Equal(outcome == "success", await server.IsAliveAsync(TestContext.Current.CancellationToken));
            if (!brokenError)
            {
                string diagnostic = Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
                Assert.Equal("log-file-write-failed", JsonNode.Parse(diagnostic)!["code"]!.ToString());
            }
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(false, "cancel")]
    [InlineData(false, "access")]
    [InlineData(true, "io")]
    [InlineData(true, "cancel")]
    [InlineData(true, "access")]
    [InlineData(false, "human-io")]
    [InlineData(false, "human-cancel")]
    [InlineData(false, "human-access")]
    public async Task Terminal_output_failure_reports_the_completed_workspace(bool ndjson, string failure)
    {
        string socket = Path.Combine(_root, "terminal-output.socket");
        string file = Path.Combine(_root, "terminal-output.yaml");
        await File.WriteAllTextAsync(file, "session_name: published\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool human = failure.StartsWith("human-", StringComparison.Ordinal);
        bool cancelled = failure.EndsWith("cancel", StringComparison.Ordinal);
        using TerminalWriter output = new(ndjson, cancelled ? cancellation : null, failure.EndsWith("access", StringComparison.Ordinal), human);
        using StringWriter error = new();
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", .. (human ? Array.Empty<string>() : [ndjson ? "--ndjson" : "--json"])], output, error, _root, cancellationToken: cancellation.Token);
            Assert.Equal(cancelled ? 130 : 1, code);
            if (human)
            {
                Assert.Contains("Recorded load results:", error.ToString(), StringComparison.Ordinal);
                Assert.Contains("published", error.ToString(), StringComparison.Ordinal);
                Assert.Contains("created", error.ToString(), StringComparison.Ordinal);
                Assert.Equal(0, (await server.ExecuteCommandAsync(["has-session", "-t", "=published"], TestContext.Current.CancellationToken)).ExitCode);
                return;
            }
            JsonNode diagnostic = JsonNode.Parse(error.ToString())!;
            Assert.Equal(cancelled ? "cancelled" : "output-failed", diagnostic["code"]!.ToString());
            JsonNode effects = Assert.IsAssignableFrom<JsonNode>(diagnostic["effects"]);
            Assert.Equal("created", effects["results"]![0]!["status"]!.ToString());
            string session = effects["results"]![0]!["session_id"]!.ToString();
            Assert.Equal(0, (await server.ExecuteCommandAsync(["has-session", "-t", session], TestContext.Current.CancellationToken)).ExitCode);
            JsonNode[] records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Single(records, item => ndjson ? item["event"]?.ToString() is "completed" or "failed" : item["status"] is not null);
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("inherited")]
    [InlineData("same-path")]
    [InlineData("other-path")]
    [InlineData("other-name")]
    [InlineData("restarted")]
    public async Task Append_authenticates_the_current_panes_server(string selection)
    {
        string socket = Path.Combine(_root, "current,with,commas");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = _root };
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        Server current = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketPath: socket, configurationFile: "/dev/null", childEnvironment: environment));
        Server other = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketName: "other", configurationFile: "/dev/null", childEnvironment: environment));
        try
        {
            string pane = await Execute(current, "new-session", "-d", "-s", "current", "-P", "-F", "#{pane_id}");
            Assert.Equal(pane, await Execute(other, "new-session", "-d", "-s", "other", "-P", "-F", "#{pane_id}"));
            environment["TMUX"] = await Execute(current, "display-message", "-p", "#{socket_path},#{pid},0");
            environment["TMUX_PANE"] = pane;
            if (selection == "restarted")
            {
                await current.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(pane, await Execute(current, "new-session", "-d", "-s", "replacement", "-P", "-F", "#{pane_id}"));
                Assert.NotEqual(environment["TMUX"], await Execute(current, "display-message", "-p", "#{socket_path},#{pid},0"));
            }
            string file = Path.Combine(_root, "append.yaml");
            await File.WriteAllTextAsync(file, "session_name: append\nwindows: [{window_name: added, panes: [null]}]", TestContext.Current.CancellationToken);
            string[] endpoint = selection switch
            {
                "same-path" => ["-S", socket],
                "other-path" => ["-S", await Execute(other, "display-message", "-p", "#{socket_path}")],
                "other-name" => ["-L", "other"],
                _ => [],
            };
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "--append", "--json", .. endpoint], output, error, _root, environment, TestContext.Current.CancellationToken);
            bool mismatch = selection.StartsWith("other", StringComparison.Ordinal) || selection == "restarted";
            Assert.Equal(mismatch ? 1 : 0, code);
            if (mismatch)
            {
                Assert.Empty(output.ToString());
                Assert.Equal(selection == "restarted" ? "stale-environment" : "endpoint-mismatch", JsonNode.Parse(error.ToString())!["code"]!.ToString());
            }
            else
            {
                JsonNode result = JsonNode.Parse(output.ToString())!["results"]![0]!;
                Assert.Equal("appended", result["status"]!.ToString());
                Assert.Equal("current", result["session_name"]!.ToString());
            }
            Assert.Equal(mismatch ? "1" : "2", await Execute(current, "display-message", "-p", "#{session_windows}"));
            Assert.Equal("1", await Execute(other, "display-message", "-p", "#{session_windows}"));
        }
        finally
        {
            if (await current.IsAliveAsync(TestContext.Current.CancellationToken)) await current.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
            if (await other.IsAliveAsync(TestContext.Current.CancellationToken)) await other.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("move-during")]
    [InlineData("moved-before")]
    [InlineData("linked")]
    public async Task Append_retains_tmuxs_resolved_session_across_inputs(string topology)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "retained.socket");
        string tmux = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "original", "-P", "-F", "#{pane_id}");
            string window = await Execute(server, "display-message", "-p", "-t", pane, "#{window_id}");
            await Execute(server, "new-window", "-d", "-t", "=original:", "-n", "keep-original");
            await Execute(server, "new-session", "-d", "-s", "other");
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(server, "display-message", "-p", "-t", pane, "#{socket_path},#{pid},#{session_id}"),
                ["TMUX_PANE"] = pane,
            };
            environment["TMUX"] = environment["TMUX"]!.Replace(",$", ",", StringComparison.Ordinal);
            if (topology is "moved-before" or "linked")
                await Execute(server, topology == "linked" ? "link-window" : "move-window", "-s", window, "-t", "=other:");
            Server observed = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketPath: socket, childEnvironment: environment));
            string selected = await Execute(observed, "display-message", "-p", "-t", pane, "#{session_id}");
            string selectedName = await Execute(observed, "display-message", "-p", "-t", pane, "#{session_name}");
            if (topology == "moved-before") Assert.Equal("other", selectedName);
            string[] paths = [Path.Combine(_root, "first.json"), Path.Combine(_root, "second.json")];
            for (int index = 0; index < paths.Length; index++)
            {
                JsonObject document = DocumentStore.Parse("session_name: ignored\nwindows: [{window_name: appended-" + index + ", panes: [null]}]");
                if (topology == "moved-before")
                {
                    document["plugins"] = new JsonArray();
                    document["workspace_builder"] = null;
                }
                if (index == 0 && topology == "move-during")
                {
                    string script = Path.Combine(_root, "move-window");
                    await File.WriteAllTextAsync(script, "#!/bin/sh\nexec \"$REAL_TMUX\" -S \"$CURRENT_SOCKET\" move-window -s \"$CURRENT_WINDOW\" -t '=other:'\n", token);
                    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    document["before_script"] = script;
                    environment["REAL_TMUX"] = tmux;
                    environment["CURRENT_SOCKET"] = socket;
                    environment["CURRENT_WINDOW"] = window;
                }
                await File.WriteAllTextAsync(paths[index], document.ToJsonString(), token);
            }
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", .. paths, "--append", "--json", "-S", socket], output, error, _root, environment, token);
            Assert.True(code == 0, error.ToString());
            JsonArray results = JsonNode.Parse(output.ToString())!["results"]!.AsArray();
            Assert.Equal(2, results.Count);
            Assert.All(results, result =>
            {
                Assert.Equal(selected, result!["session_id"]!.ToString());
                Assert.Equal(selectedName, result["session_name"]!.ToString());
            });
            string appended = await Execute(server, "list-windows", "-t", selected, "-F", "#{window_name}");
            Assert.Contains("appended-0", appended, StringComparison.Ordinal);
            Assert.Contains("appended-1", appended, StringComparison.Ordinal);
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Fact]
    public async Task Append_refuses_retargeted_daemon_before_global_mutation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string currentSocket = Path.Combine(_root, "owned.socket");
        string otherSocket = Path.Combine(_root, "other.socket");
        string selectedSocket = Path.Combine(_root, "selected.socket");
        string tmux = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Server current = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketPath: currentSocket, configurationFile: "/dev/null"));
        Server other = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: tmux, socketPath: otherSocket, configurationFile: "/dev/null"));
        try
        {
            string pane = await Execute(current, "new-session", "-d", "-s", "borrowed", "-P", "-F", "#{pane_id}");
            Assert.Equal(pane, await Execute(other, "new-session", "-d", "-s", "unrelated", "-P", "-F", "#{pane_id}"));
            File.CreateSymbolicLink(selectedSocket, currentSocket);
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(current, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = pane,
                ["SELECTED_SOCKET"] = selectedSocket,
                ["OTHER_SOCKET"] = otherSocket,
            };
            string script = Path.Combine(_root, "retarget");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nexec ln -sfn \"$OTHER_SOCKET\" \"$SELECTED_SOCKET\"\n", token);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string first = Path.Combine(_root, "first.json");
            string second = Path.Combine(_root, "second.json");
            JsonObject document = DocumentStore.Parse("session_name: unused\nwindows: [{window_name: completed, panes: [null]}]");
            await File.WriteAllTextAsync(first, document.ToJsonString(), token);
            document["before_script"] = script;
            document["global_options"] = new JsonObject { ["@must-not-change"] = "bad" };
            await File.WriteAllTextAsync(second, document.ToJsonString(), token);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", first, second, "--append", "--json", "-S", selectedSocket], output, error, _root, environment, token);
            string untouched = await Execute(other, "show-options", "-gqv", "@must-not-change");
            Assert.True(code == 1 && untouched.Length == 0, $"Exit {code}, unrelated option '{untouched}', result {output}, error {error}");
            JsonNode summary = JsonNode.Parse(output.ToString())!;
            Assert.Equal("partial", summary["status"]!.ToString());
            Assert.Single(summary["results"]!.AsArray());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal("stale-server", issue["code"]!.ToString());
            Assert.Equal("session-options", issue["failed_stage"]!.ToString());
            Assert.False(issue["created"]!.GetValue<bool>());
            Assert.False(issue["removed"]!.GetValue<bool>());
            Assert.Equal("2", await Execute(current, "display-message", "-p", "#{session_windows}"));
            Assert.Equal("1", await Execute(other, "display-message", "-p", "#{session_windows}"));
            Assert.Empty(await Execute(current, "show-options", "-gqv", "@must-not-change"));
        }
        finally
        {
            if (await current.IsAliveAsync(token)) await current.KillAsync(cancellationToken: token);
            if (await other.IsAliveAsync(token)) await other.KillAsync(cancellationToken: token);
        }
    }

    [Theory]
    [InlineData("plugins: [example.Plugin]")]
    [InlineData("workspace_builder: example.Builder")]
    public async Task Append_refuses_python_delegation_before_running_any_input(string extension)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "python-append.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "borrowed", "-P", "-F", "#{pane_id}");
            string python = Path.Combine(_root, "python-sentinel");
            string trace = Path.Combine(_root, "python-trace");
            await File.WriteAllTextAsync(python, "#!/bin/sh\nprintf 'python\\n' >> \"$PYTHON_TRACE\"\nif test \"$1\" = -c; then printf '1.74.0\\n'; fi\n", token);
            File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = pane,
                ["TMUX_WORKSPACE_PYTHON"] = python,
                ["PYTHON_TRACE"] = trace,
            };
            string first = Path.Combine(_root, "native.yaml");
            string second = Path.Combine(_root, "extension.yaml");
            const string native = "session_name: ignored\nwindows: [{panes: [null]}]\n";
            await File.WriteAllTextAsync(first, native, token);
            await File.WriteAllTextAsync(second, native + extension, token);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", first, second, "--append", "--ndjson", "-S", socket], output, error, _root, environment, token);
            Assert.True(code == 2 && !File.Exists(trace), $"Exit {code}, Python invoked {File.Exists(trace)}, output {output}, error {error}");
            Assert.Empty(output.ToString());
            Assert.Equal("unsupported-append-extensions", JsonNode.Parse(error.ToString())!["code"]!.ToString());
            Assert.Equal("1", await Execute(server, "display-message", "-p", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    private static async Task<string> Execute(Server server, params string[] arguments)
    {
        TmuxCommandResult result = await server.ExecuteCommandAsync(arguments, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        return System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span).TrimEnd('\n');
    }

    [Theory]
    [InlineData("reuse")]
    [InlineData("freeze")]
    public async Task Session_lookup_requires_the_exact_name(string command)
    {
        string socket = Path.Combine(_root, "exact.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            await Execute(server, "new-session", "-d", "-s", "existing-long");
            if (command == "reuse")
            {
                string sessionId = await Execute(server, "new-session", "-d", "-s", "existing", "-P", "-F", "#{session_id}");
                string file = Path.Combine(_root, "reuse.yaml");
                await File.WriteAllTextAsync(file, "session_name: existing\nbefore_script: /bin/false\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
                var result = await Run("load", file, "-d", "-S", socket, "--json");
                Assert.True(result.Code == 0, result.Error + result.Output);
                Assert.Equal("reused", JsonNode.Parse(result.Output)!["results"]![0]!["status"]!.ToString());
                Assert.Equal(sessionId, JsonNode.Parse(result.Output)!["results"]![0]!["session_id"]!.ToString());
                Assert.Equal("1", await Execute(server, "display-message", "-p", "-t", "=existing:", "#{session_windows}"));
            }
            else
            {
                var result = await Run("freeze", "existing", "-S", socket, "--json");
                Assert.Equal(1, result.Code);
                Assert.Empty(result.Output);
                Assert.Equal("session-not-found", JsonNode.Parse(result.Error)!["code"]!.ToString());
            }
            Assert.Equal("1", await Execute(server, "display-message", "-p", "-t", "=existing-long:", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("script")]
    [InlineData("options")]
    [InlineData("cancel")]
    [InlineData("next-input")]
    public async Task Partial_load_reports_completed_inputs_and_owned_session_state(string failure)
    {
        string socket = Path.Combine(_root, "partial.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using CancellingWriter output = new(failure is "cancel" or "next-input" ? cancellation : null, failure == "next-input" ? "workspace-completed" : "script-output");
        using StringWriter error = new();
        string[] files = ["complete", "failed", "unscheduled"];
        for (int index = 0; index < files.Length; index++)
        {
            string name = files[index];
            files[index] = Path.Combine(_root, name + ".yaml");
            string invalid = failure switch
            {
                "script" => "before_script: /bin/false\n",
                "cancel" => "before_script: /bin/sh -c 'printf ready; sleep 60'\n",
                _ => "options: {invalid-option: value}\n",
            };
            await File.WriteAllTextAsync(files[index], "session_name: " + name + "\n" + (index == 1 ? invalid : "") + "windows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        }
        try
        {
            await Execute(server, "new-session", "-d", "-s", "keeper");
            int code = await CliRunner.RunAsync(["load", .. files, "-d", "-S", socket, "--ndjson"], output, error, _root, cancellationToken: cancellation.Token);
            Assert.Equal(failure is "cancel" or "next-input" ? 130 : 1, code);
            JsonNode[] events = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal(Enumerable.Range(1, events.Length), events.Select(item => item["sequence"]!.GetValue<int>()));
            Assert.Single(events, item => item["event"]!.ToString() is "failed" or "completed");
            Assert.Equal("failed", events[^1]["event"]!.ToString());
            JsonNode summary = events[^1]["data"]!;
            Assert.Equal("partial", summary["status"]!.ToString());
            Assert.Equal("complete", Assert.Single(summary["results"]!.AsArray())!["session_name"]!.ToString());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal(1, issue["input_index"]!.GetValue<int>());
            Assert.Equal(failure != "next-input", issue["created"]!.GetValue<bool>());
            Assert.Equal(failure is "script" or "cancel", issue["removed"]!.GetValue<bool>());
            Assert.Equal(failure switch { "options" => "session-options", "next-input" => "workspace-started", _ => "before-script" }, issue["failed_stage"]!.ToString());
            Assert.Equal(failure == "next-input" ? null : "session-created", issue["completed_stage"]?.ToString());
            string[] expected = failure == "options" ? ["complete", "failed", "keeper"] : ["complete", "keeper"];
            Assert.Equal(expected, (await Execute(server, "list-sessions", "-F", "#{session_name}")).Split('\n').Order(StringComparer.Ordinal));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Multi_input_session_override_changes_only_the_final_workspace()
    {
        string socket = Path.Combine(_root, "override.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        string[] paths = [Path.Combine(_root, "first.yaml"), Path.Combine(_root, "second.yaml")];
        for (int index = 0; index < paths.Length; index++) await File.WriteAllTextAsync(paths[index], "session_name: " + Path.GetFileNameWithoutExtension(paths[index]) + "\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        try
        {
            var result = await Run("load", paths[0], paths[1], "-s", "renamed", "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.Equal(0, result.Code);
            JsonArray results = JsonNode.Parse(result.Output)!["results"]!.AsArray();
            string[] expectedNames = ["first", "renamed"];
            Assert.Equal(expectedNames, results.Select(item => item!["session_name"]!.ToString()));
            Assert.All(results, item => Assert.Equal("created", item!["status"]!.ToString()));
            Assert.Equal("first\nrenamed", await Execute(server, "list-sessions", "-F", "#{session_name}"));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task First_append_failure_reports_retained_borrowed_session_changes()
    {
        string socket = Path.Combine(_root, "borrowed.socket");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "borrowed", "-P", "-F", "#{pane_id}");
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = pane,
            };
            string file = Path.Combine(_root, "borrowed.yaml");
            await File.WriteAllTextAsync(file, "session_name: unused\nwindows: [{options_after: {invalid-option: value}, panes: [null]}]", TestContext.Current.CancellationToken);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "--append", "-S", socket, "--json"], output, error, _root, environment, TestContext.Current.CancellationToken);
            Assert.Equal(1, code);
            JsonNode summary = JsonNode.Parse(output.ToString())!;
            Assert.Equal("partial", summary["status"]!.ToString());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.False(issue["created"]!.GetValue<bool>());
            Assert.False(issue["removed"]!.GetValue<bool>());
            Assert.True(issue["changed"]!.GetValue<bool>());
            Assert.Equal("2", await Execute(server, "display-message", "-p", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("always")]
    [InlineData("auto")]
    public async Task Blank_panes_skip_readiness_probes(string readiness)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "blank.socket");
        string file = Path.Combine(_root, "blank.yaml");
        string trace = Path.Combine(_root, "tmux-arguments");
        string wrapper = Path.Combine(_root, "tmux-wrapper");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"$TRACE\"\nexec \"$REAL_TMUX\" \"$@\"\n", token);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(file, "session_name: blank\nworkspace_builder_options: {pane_readiness: " + readiness + "}\noptions: {default-command: 'sleep 30'}\nwindows: [{panes: [null, null]}]\n", token);
        string binary = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["LIBTMUX_TMUX"] = wrapper,
            ["REAL_TMUX"] = binary,
            ["TRACE"] = trace,
        };
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: binary, socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], output, error, _root, environment, token);
            Assert.True(code == 0, error.ToString());
            string arguments = await File.ReadAllTextAsync(trace, token);
            Assert.DoesNotContain("#{pane_current_command}", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("#{cursor_x},#{cursor_y}", arguments, StringComparison.Ordinal);
            Assert.Equal("2", await Execute(server, "display-message", "-p", "-t", "=blank:", "#{window_panes}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_colors_do_not_run_tmux_or_python(bool extensions)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "colors.socket");
        string file = Path.Combine(_root, "colors.yaml");
        string trace = Path.Combine(_root, "executed-arguments");
        string pythonTrace = Path.Combine(_root, "python-arguments");
        string wrapper = Path.Combine(_root, "runtime-wrapper");
        string python = Path.Combine(_root, "python-wrapper");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"$TRACE\"\nexec \"$REAL_TMUX\" \"$@\"\n", token);
        await File.WriteAllTextAsync(python, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"$PYTHON_TRACE\"\nexit 1\n", token);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(file, "session_name: colors\nwindows: [{panes: [null]}]\n" + (extensions ? "plugins: [example.Plugin]\n" : ""), token);
        string binary = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["LIBTMUX_TMUX"] = wrapper,
            ["TMUX_WORKSPACE_PYTHON"] = python,
            ["REAL_TMUX"] = binary,
            ["TRACE"] = trace,
            ["PYTHON_TRACE"] = pythonTrace,
        };
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: binary, socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            await Execute(server, "new-session", "-d", "-s", "keeper");
            string[] topology = ["list-panes", "-a", "-F", "#{session_id}:#{window_id}:#{pane_id}"];
            string before = await Execute(server, topology);
            foreach (string colors in new[] { "-8", "--88-colors" })
                foreach (string mode in new[] { "", "--json", "--ndjson" })
                {
                    using StringWriter output = new();
                    using StringWriter error = new();
                    string[] outputMode = mode.Length == 0 ? [] : [mode];
                    int code = await CliRunner.RunAsync(["load", file, colors, "-d", "-S", socket, .. outputMode], output, error, _root, environment, token);
                    Assert.False(File.Exists(trace), "Rejected color mode ran tmux.");
                    Assert.False(File.Exists(pythonTrace), "Rejected color mode ran Python.");
                    Assert.Equal(before, await Execute(server, topology));
                    Assert.Equal(2, code);
                    Assert.Empty(output.ToString());
                    Assert.Contains("88-color", error.ToString(), StringComparison.Ordinal);
                }
            if (!extensions)
            {
                using StringWriter output = new();
                using StringWriter error = new();
                int code = await CliRunner.RunAsync(["load", file, "-2", "-d", "-S", socket, "--json"], output, error, _root, environment, token);
                Assert.True(code == 0, error.ToString());
                Assert.Contains("-2", (await File.ReadAllTextAsync(trace, token)).Split('\n'));
                Assert.Equal("colors\nkeeper", await Execute(server, "list-sessions", "-F", "#{session_name}"));
            }
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    private sealed class CancellingWriter(CancellationTokenSource? cancellation, string eventName = "script-output") : StringWriter
    {
        private bool _pending;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            base.WriteLine(value.ToString());
            _pending |= value.Span.Contains(("\"event\":\"" + eventName + "\"").AsSpan(), StringComparison.Ordinal);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending) { _pending = false; cancellation?.Cancel(); }
            return Task.CompletedTask;
        }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("\"event\":\"" + eventName + "\"", StringComparison.Ordinal) == true) cancellation?.Cancel();
        }
    }

    private CliContext Context(TextWriter output) => new(output, TextWriter.Null, _root, System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal), TestContext.Current.CancellationToken);

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync(args, output, error, _root, cancellationToken: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private sealed class ClearFailureWriter(bool inaccessible) : StringWriter
    {
        internal int Failures { get; private set; }
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && buffer.Span.Contains("\u001b[1A", StringComparison.Ordinal)) { Failures++; throw inaccessible ? new UnauthorizedAccessException("Clear failed once.") : new IOException("Clear failed once."); }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("reader closed");
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default) => throw new IOException("reader closed");
    }

    private sealed class BlockingWriter : StringWriter
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingLogWriter(string trigger) : StringWriter
    {
        private bool _failed;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            _failed |= value.Span.Contains(("\"event\":\"" + trigger + "\"").AsSpan(), StringComparison.Ordinal);
            return _failed ? Task.FromException(new IOException("log file full")) : base.WriteLineAsync(value, cancellationToken);
        }
        public override ValueTask DisposeAsync() => _failed ? ValueTask.FromException(new IOException("log flush failed")) : base.DisposeAsync();
    }

    private sealed class TerminalWriter(bool ndjson, CancellationTokenSource? cancellation, bool inaccessible = false, bool human = false) : StringWriter
    {
        private bool _terminal;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            JsonNode record = JsonNode.Parse(value.ToString())!;
            _terminal = ndjson ? record["event"]?.ToString() == "completed" : record["status"]?.ToString() == "ok";
            return base.WriteLineAsync(value, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_terminal && !human) return base.FlushAsync(cancellationToken);
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw inaccessible ? new UnauthorizedAccessException("terminal output inaccessible") : new IOException("terminal output closed");
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
