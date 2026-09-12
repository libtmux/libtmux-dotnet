using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class RegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-regression-" + Guid.NewGuid().ToString("N"));
    public RegressionTests() => Directory.CreateDirectory(_root);

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
            var result = await Run("shell", "bridge", "-S", socket, "--code", "--no-startup", "-c", "print(session.session_name)", "--json");
            Assert.Equal(0, result.Code);
            Assert.Contains("bridge", JsonNode.Parse(result.Output)!["stdout"]!.ToString(), StringComparison.Ordinal);
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Python_bridge_leaves_log_file_ownership_with_the_native_cli(bool equals)
    {
        string file = Path.Combine(_root, "extension.yaml");
        string python = Path.Combine(_root, "python");
        string trace = Path.Combine(_root, "arguments");
        string log = Path.Combine(_root, "bridge.ndjson");
        await File.WriteAllTextAsync(file, "session_name: bridge\nplugins: [example]\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(python, $$"""
            #!/bin/sh
            if test "$1" = -c; then printf '1.74.0\n'; exit 0; fi
            printf '%s\n' "$@" > '{{trace}}'
            printf 'bridge \342\230\203\033[31m\n'
            printf 'warning\rline\n' >&2
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] level = equals ? ["--log-level=debug"] : ["--log-level", "debug"];
        string[] destination = equals ? ["--log-file=bridge.ndjson"] : ["--log-file", "bridge.ndjson"];
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_WORKSPACE_PYTHON"] = python };
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync([.. level, "load", file, "-d", .. destination, "--json"], output, error, _root, environment, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Equal("ok", JsonNode.Parse(output.ToString())!["status"]!.ToString());
        Assert.Equal(["load", file, "-d"], (await File.ReadAllLinesAsync(trace, TestContext.Current.CancellationToken))[3..]);
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Terminal_output_failure_reports_the_completed_workspace(bool ndjson, bool cancelled)
    {
        string socket = Path.Combine(_root, "terminal-output.socket");
        string file = Path.Combine(_root, "terminal-output.yaml");
        await File.WriteAllTextAsync(file, "session_name: published\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using TerminalWriter output = new(ndjson, cancelled ? cancellation : null);
        using StringWriter error = new();
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", ndjson ? "--ndjson" : "--json"], output, error, _root, cancellationToken: cancellation.Token);
            Assert.Equal(cancelled ? 130 : 1, code);
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

    private sealed class TerminalWriter(bool ndjson, CancellationTokenSource? cancellation) : StringWriter
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
            if (!_terminal) return base.FlushAsync(cancellationToken);
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("terminal output closed");
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
