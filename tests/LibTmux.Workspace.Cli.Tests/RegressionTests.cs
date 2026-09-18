using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
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
        display.Script("stdout", string.Concat(Enumerable.Repeat("e\u0301\U0001F642", 3000)));
        string line = display.Render().Split("\r\n")[1];
        Assert.True(line.StartsWith("e\u0301", StringComparison.Ordinal) || line.StartsWith("\U0001F642", StringComparison.Ordinal));
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
    public async Task Resized_progress_erases_its_frame_before_the_display_is_dropped()
    {
        using StringWriter error = new();
        CliContext context = Context(TextWriter.Null) with { Error = error };
        Invocation invocation = new CommandLine().Parse(["load", "workspace", "-d"]);
        (int Width, int Height) terminal = (80, 10);
        ProgressDisplay progress = new(new ProgressOptions("START", 0), terminal.Width, terminal.Height, false, () => terminal);
        await using Output output = new(context, invocation, null, progress);
        await output.ProgressAsync(display => display.StartBridge(), force: true);
        string painted = progress.ClearSequence;
        Assert.NotEmpty(painted);
        error.GetStringBuilder().Clear();
        terminal = (100, 10);

        await output.HandoffAsync();

        Assert.Equal(painted, error.ToString());
    }

    [Theory]
    [InlineData("TMUXP_DEFAULT_COLUMNS")]
    [InlineData("COLUMNS")]
    public async Task Invalid_dimension_names_the_variable_that_carried_it(string variable)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string file = Path.Combine(_root, "dimension.yaml");
        await File.WriteAllTextAsync(file, "session_name: dimension\nwindows: [{panes: [null]}]", token);
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["LIBTMUX_TMUX"] = Path.Combine(_root, "missing-tmux"),
            ["TMUX"] = null,
            ["TMUX_PANE"] = null,
            ["TMUXP_DEFAULT_COLUMNS"] = variable == "TMUXP_DEFAULT_COLUMNS" ? "wide" : null,
            ["COLUMNS"] = "wide",
        };
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await CliRunner.RunAsync(["load", file, "-d", "--json"], output, error, _root, environment, token);

        Assert.Equal(2, code);
        Assert.Empty(output.ToString());
        JsonNode diagnostic = JsonNode.Parse(error.ToString())!;
        Assert.Equal("invalid_dimension", diagnostic["code"]!.ToString());
        Assert.StartsWith(variable + " must be", diagnostic["message"]!.ToString(), StringComparison.Ordinal);
    }

    // Every string scalar a YAML 1.1 (PyYAML/tmuxp) or 1.2 resolver would
    // read as bool, null, int or float must round-trip as the same string
    // through convert's YAML emitter. Verified separately
    // against `uvx tmuxp 1.74.0`, which reads this port's emitted YAML back
    // with every one of these names unchanged.
    [Fact]
    public void Emitted_yaml_quotes_every_ambiguous_scalar()
    {
        string[] names = ["yes", "Yes", "1.0", "null", "Null", "on", "off", "08", "0x1F", "0o7", "1e3", ".inf", ".nan", "1_000", "1:30", "~", "true", "false", "y", "n", ""];
        JsonObject document = new()
        {
            ["session_name"] = "quoting",
            ["windows"] = new JsonArray(
                [.. names.Select(name => (JsonNode)new JsonObject { ["window_name"] = name, ["panes"] = new JsonArray((JsonNode?)null) })]),
        };

        string yaml = DocumentStore.Encode(document, "yaml");
        JsonObject reloaded = DocumentStore.Parse(yaml);

        string[] roundTripped = [.. reloaded["windows"]!.AsArray().Select(window => window!["window_name"]!.ToString())];
        Assert.Equal(names, roundTripped);
    }

    // `<<: *anchor` / `<<: [*a, *b]` merge keys, with explicit keys
    // overriding merged ones, at every mapping level.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_keys_resolve_with_explicit_keys_winning(bool explicitFirst)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "merge.socket");
        string file = Path.Combine(_root, "merge.yaml");
        string second = explicitFirst
            ? "  - window_name: b\n    <<: *base\n"
            : "  - <<: *base\n    window_name: b\n";
        await File.WriteAllTextAsync(
            file,
            "session_name: merged\nwindows:\n  - &base\n    window_name: a\n    panes: [null]\n" + second,
            token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], TextWriter.Null, TextWriter.Null, _root, null, token);

            Assert.Equal(0, code);
            Assert.Equal("a\nb", await Execute(server, "list-windows", "-t", "merged", "-F", "#{window_name}"));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // A key starting with "x-", at any level, is inert -- accepted and
    // ignored at load, while an unrelated unknown key is still refused and
    // the refusal names the "x-" escape hatch.
    [Fact]
    public async Task Extension_keys_are_inert_and_the_refusal_names_the_escape()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string accepted = Path.Combine(_root, "x-ok.yaml");
        await File.WriteAllTextAsync(
            accepted,
            "session_name: xok\nx-defaults: &shared\n  x-note: unused\nwindows: [{window_name: main, x-window-note: unused, panes: [null]}]",
            token);
        string refused = Path.Combine(_root, "x-bad.yaml");
        await File.WriteAllTextAsync(refused, "session_name: xbad\nbogus_top_level_key: 1\nwindows: [{panes: [null]}]", token);
        using StringWriter okOutput = new();
        using StringWriter okError = new();
        using StringWriter badOutput = new();
        using StringWriter badError = new();

        int okCode = await CliRunner.RunAsync(["load", accepted, "-d", "--json"], okOutput, okError, _root, null, token);
        int badCode = await CliRunner.RunAsync(["load", refused, "-d", "--json"], badOutput, badError, _root, null, token);

        Assert.Equal(0, okCode);
        Assert.Empty(okError.ToString());
        Assert.Equal(1, badCode);
        JsonNode diagnostic = JsonNode.Parse(badError.ToString())!;
        Assert.Equal("unsupported_key", diagnostic["code"]!.ToString());
        Assert.Contains("'x-'", diagnostic["message"]!.ToString(), StringComparison.Ordinal);
    }

    // A missing tmux executable is tmux_unavailable, distinct from the
    // generic executable_unavailable shared by EDITOR/before_script.
    [Fact]
    public async Task Missing_tmux_executable_reports_its_own_code()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string file = Path.Combine(_root, "unreachable.yaml");
        await File.WriteAllTextAsync(file, "session_name: unreachable\nwindows: [{panes: [null]}]", token);
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["TMUX"] = null,
            ["TMUX_PANE"] = null,
            ["LIBTMUX_TMUX"] = null,
            ["PATH"] = _root,
        };
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await CliRunner.RunAsync(["load", file, "-d", "--json"], output, error, _root, environment, token);

        Assert.Equal(1, code);
        Assert.Empty(output.ToString());
        Assert.Equal("tmux_unavailable", JsonNode.Parse(error.ToString())!["code"]!.ToString());
    }

    // A session built without asking the terminal its size gets stretched
    // once a client attaches -- main-pane layouts come out at the wrong
    // ratio, and typing into a pane that is about to be resized by that
    // stretch can lose the keystrokes under zsh. COLUMNS/LINES is
    // the only terminal size this harness can fake without a real pty; it
    // must win over TMUXP_DEFAULT_COLUMNS/ROWS, matching go's sessionDimensions.
    [Fact]
    public async Task Session_size_prefers_COLUMNS_and_LINES_over_the_configured_default()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "sized.socket");
        string file = Path.Combine(_root, "sized.yaml");
        await File.WriteAllTextAsync(file, "session_name: sized\nwindows: [{panes: [null]}]", token);
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["TMUX"] = null,
            ["TMUX_PANE"] = null,
            ["TMUXP_DETECT_TERMINAL_SIZE"] = "1",
            ["TMUXP_DEFAULT_COLUMNS"] = "200",
            ["TMUXP_DEFAULT_ROWS"] = "50",
            ["COLUMNS"] = "123",
            ["LINES"] = "40",
        };
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", "keeper");
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], output, error, _root, environment, token);

            Assert.Equal(0, code);
            Assert.Equal("123x40", await Execute(server, "display-message", "-p", "-t", "=sized:", "#{window_width}x#{window_height}"));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    [Fact]
    public async Task Disabled_terminal_detection_passes_no_session_size()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "unsized.socket");
        string file = Path.Combine(_root, "unsized.yaml");
        await File.WriteAllTextAsync(file, "session_name: unsized\nwindows: [{panes: [null]}]", token);
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
        {
            ["TMUX"] = null,
            ["TMUX_PANE"] = null,
            ["TMUXP_DETECT_TERMINAL_SIZE"] = "0",
            ["TMUXP_DEFAULT_COLUMNS"] = "200",
            ["TMUXP_DEFAULT_ROWS"] = "50",
        };
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", "keeper");
            await Execute(server, "set-option", "-g", "default-size", "45x11");
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], output, error, _root, environment, token);

            Assert.Equal(0, code);
            Assert.Equal("45x11", await Execute(server, "display-message", "-p", "-t", "=unsized:", "#{window_width}x#{window_height}"));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
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
        Assert.Equal("log_file_unavailable", JsonNode.Parse(error.ToString())!["code"]!.ToString());
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            var result = await Run("--log-level", "debug", "load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson", "--log-file", "operation.ndjson");
            Assert.True(result.Code == 0, result.Error + result.Output);
            JsonNode[] events = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            // A consumer needs to know when the script began, when it ended,
            // with what status, and which input it belonged to -- go already
            // reports all four; script-output alone did not.
            int started = Array.FindIndex(events, item => item["event"]!.ToString() == "script-started");
            int streamed = Array.FindIndex(events, item => item["event"]!.ToString() == "script-output");
            int finished = Array.FindIndex(events, item => item["event"]!.ToString() == "script-completed");
            Assert.True(Array.FindIndex(events, item => item["event"]!.ToString() == "session-created") < started);
            Assert.True(started < streamed);
            Assert.True(streamed < finished);
            Assert.Equal(0, events[started]["input_index"]!.GetValue<int>());
            Assert.Equal(0, events[streamed]["input_index"]!.GetValue<int>());
            JsonNode completedScript = events[finished];
            Assert.Equal(0, completedScript["input_index"]!.GetValue<int>());
            Assert.Equal(0, completedScript["child_status"]!.GetValue<int>());
            Assert.False(completedScript["truncated"]!.GetValue<bool>());
            Assert.Single(events, item => item["event"]!.ToString() == "completed");
            string sessionId = events.Single(item => item["event"]!.ToString() == "session-created")["session_id"]!.ToString();
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

    // ndjson records are flat, and window/pane creation is paired with a
    // matching completion event.
    [Fact]
    public async Task Ndjson_events_are_flat_and_report_pane_and_window_completion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "contract.socket");
        string file = Path.Combine(_root, "contract.yaml");
        await File.WriteAllTextAsync(file, "session_name: contract\nwindows: [{window_name: only, panes: [null, null]}]\n", token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            var result = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson");
            Assert.Empty(result.Error);
            Assert.Equal(0, result.Code);
            JsonNode[] events = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();

            Assert.All(events, item => Assert.Null(item["data"]));
            Assert.Equal(1, events.Single(item => item["event"]!.ToString() == "started")["inputs"]!.GetValue<int>());

            JsonNode windowCreated = events.Single(item => item["event"]!.ToString() == "window-created");
            Assert.Equal(0, windowCreated["input_index"]!.GetValue<int>());
            Assert.NotNull(windowCreated["session_id"]);
            Assert.NotNull(windowCreated["window_id"]);
            Assert.Equal(1, windowCreated["window_index"]!.GetValue<int>());

            JsonNode[] paneCreated = [.. events.Where(item => item["event"]!.ToString() == "pane-created")];
            Assert.Equal(2, paneCreated.Length);
            Assert.Equal([1, 2], paneCreated.Select(item => item["pane_index"]!.GetValue<int>()));
            Assert.All(paneCreated, item => Assert.Equal(0, item["input_index"]!.GetValue<int>()));
            Assert.All(paneCreated, item => Assert.Equal(windowCreated["session_id"]!.ToString(), item["session_id"]!.ToString()));
            Assert.All(paneCreated, item => Assert.Equal(windowCreated["window_id"]!.ToString(), item["window_id"]!.ToString()));
            Assert.All(paneCreated, item => Assert.Equal(1, item["window_index"]!.GetValue<int>()));

            JsonNode[] paneCompleted = [.. events.Where(item => item["event"]!.ToString() == "pane-completed")];
            Assert.Equal(paneCreated.Select(item => item["pane_id"]!.ToString()), paneCompleted.Select(item => item["pane_id"]!.ToString()));

            JsonNode windowCompleted = events.Single(item => item["event"]!.ToString() == "window-completed");
            Assert.Equal(windowCreated["window_id"]!.ToString(), windowCompleted["window_id"]!.ToString());

            int windowCompletedIndex = Array.IndexOf(events, windowCompleted);
            int lastPaneCompletedIndex = Array.IndexOf(events, paneCompleted[^1]);
            int workspaceCompletedIndex = Array.FindIndex(events, item => item["event"]!.ToString() == "workspace-completed");
            Assert.True(lastPaneCompletedIndex < windowCompletedIndex, "pane-completed must precede window-completed.");
            Assert.True(windowCompletedIndex < workspaceCompletedIndex, "window-completed must precede workspace-completed.");
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Fact]
    public async Task Python_shell_bridge_executes_against_the_explicit_socket()
    {
        string socket = Path.Combine(_root, "python.socket");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
        await using Output output = new(context, new CommandLine().Parse(["shell", "-c", "print()", "--ndjson"]));
        Exception? error = await Record.ExceptionAsync(() => ProcessCommands.RunProcessAsync(context, output, "/bin/sh", ["-c", "while :; do printf 'long-output-line\\n'; done"], _root, true));
        Assert.IsType<IOException>(error);
        Assert.False(limit.IsCancellationRequested);
    }

    [Fact]
    public async Task Child_teardown_failure_keeps_the_failure_that_started_it()
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        ChannelWriter sink = new();
        CliContext context = Context(sink) with { CancellationToken = limit.Token };
        await using Output output = new(context, new CommandLine().Parse(["shell", "-c", "print()", "--ndjson"]));

        Task<ChildResult> child = ProcessCommands.RunProcessAsync(context, output, "/bin/sh", ["-c", "printf 'failing\\n' >&2; while :; do printf 'long-output-line\\n'; done"], _root, true);
        await sink.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        sink.Release.TrySetException(new ObjectDisposedException("sink"));
        Exception? error = await Record.ExceptionAsync(() => child);

        Assert.True(sink.Discarded, "the teardown drain never failed");
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            int code = await new ExecutionCommands(context, invocation, output).LoadAsync();
            Assert.Equal(outcome == "success" ? 0 : outcome == "cancel" ? 130 : 1, code);
            JsonNode[] events = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            JsonNode terminal = Assert.Single(events, item => item["event"]!.ToString() is "completed" or "failed");
            Assert.Equal(outcome == "success" ? "ok" : "error", terminal["status"]!.ToString());
            Assert.Equal(outcome == "success", await server.IsAliveAsync(TestContext.Current.CancellationToken));
            if (!brokenError)
            {
                string diagnostic = Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
                Assert.Equal("log_file_write_failed", JsonNode.Parse(diagnostic)!["code"]!.ToString());
            }
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    // A before_script that cannot even start (a bad path, not just a bad
    // exit) is a before_script failure like any other: script_failed, exit
    // 1, the owned session removed, the input's error entry kept. tmuxp
    // raises BeforeLoadScriptNotExists for the same condition.
    [Theory]
    [InlineData("missing")]
    [InlineData("nonzero")]
    public async Task Before_script_failure_removes_the_owned_session(string kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "bsfail-" + kind + ".socket");
        string file = Path.Combine(_root, "bsfail-" + kind + ".yaml");
        string script = kind == "missing" ? Path.Combine(_root, "missing-script") : "/bin/false";
        await File.WriteAllTextAsync(file, "session_name: bsfail\nbefore_script: " + script + "\nwindows: [{panes: [null]}]", token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], output, error, _root, null, token);

            Assert.Equal(1, code);
            Assert.Equal("script_failed", JsonNode.Parse(error.ToString())!["code"]!.ToString());
            JsonNode summary = JsonNode.Parse(output.ToString())!;
            Assert.Equal("error", summary["status"]!.ToString());
            // The failed input still gets its own results[] record: same
            // fields a completed input's record carries.
            JsonNode record = Assert.Single(summary["results"]!.AsArray())!;
            Assert.Equal(file, record["input"]!.ToString());
            Assert.Equal(0, record["input_index"]!.GetValue<int>());
            Assert.Equal("bsfail", record["session_name"]!.ToString());
            Assert.False(string.IsNullOrEmpty(record["session_id"]!.ToString()));
            Assert.False(record["reused"]!.GetValue<bool>());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal("script_failed", issue["code"]!.ToString());
            Assert.Equal(0, issue["input_index"]!.GetValue<int>());
            Assert.Equal("before-script", issue["failed_stage"]!.ToString());
            Assert.True(issue["removed"]!.GetValue<bool>());
            Assert.False(await server.IsAliveAsync(token));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // Human mode never prints a machine record: a failed load's stdout must
    // not parse as JSON at all, only the plain sentence on stderr.
    [Fact]
    public async Task Human_mode_before_script_failure_prints_no_machine_record()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "bsfail-human.socket");
        string file = Path.Combine(_root, "bsfail-human.yaml");
        await File.WriteAllTextAsync(file, "session_name: bsfailhuman\nbefore_script: /bin/false\nwindows: [{panes: [null]}]", token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "-d", "-y", "-S", socket, "-f", "/dev/null"], output, error, _root, null, token);

            Assert.Equal(1, code);
            string stdout = output.ToString();
            Assert.False(stdout.TrimStart().StartsWith('{'), $"stdout printed a machine record: {stdout}");
            Assert.DoesNotContain("schema_version", stdout, StringComparison.Ordinal);
            Assert.Contains("before_script exited with status", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // Cancellation mid-window leaves the session half built, so the load
    // removes it: a name left standing is what the next run would reuse.
    [Fact]
    public async Task Cancellation_mid_window_removes_the_session_it_created()
    {
        CancellationToken outer = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "mid-window.socket");
        string file = Path.Combine(_root, "mid-window.yaml");
        await File.WriteAllTextAsync(file, "session_name: midwindow\nwindows: [{window_name: only, panes: [null, null]}]\n", outer);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
        using CancellingWriter stdout = new(cancellation, "pane-created");
        using StringWriter stderr = new();
        CliContext context = Context(stdout) with { Error = stderr, CancellationToken = cancellation.Token };
        Invocation invocation = new CommandLine().Parse(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson"]);
        await using Output output = new(context, invocation);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            int code = await new ExecutionCommands(context, invocation, output).LoadAsync();
            Assert.Equal(130, code);
            JsonNode[] events = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.DoesNotContain(events, item => item["event"]!.ToString() == "window-completed");
            Assert.True(events[^1]["removed"]?.GetValue<bool>() ?? events[^1]["errors"]![0]!["removed"]!.GetValue<bool>());
            TmuxCommandResult listed = await server.ExecuteCommandAsync(["has-session", "-t", "=midwindow"], outer);
            Assert.NotEqual(0, listed.ExitCode);
        }
        finally { if (await server.IsAliveAsync(outer)) await server.KillAsync(cancellationToken: outer); }
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
            Assert.Equal(cancelled ? "interrupted" : "output_failed", diagnostic["code"]!.ToString());
            JsonNode effects = Assert.IsAssignableFrom<JsonNode>(diagnostic["effects"]);
            Assert.Equal("created", effects["results"]![0]!["status"]!.ToString());
            string session = effects["results"]![0]!["session_id"]!.ToString();
            Assert.Equal(0, (await server.ExecuteCommandAsync(["has-session", "-t", session], TestContext.Current.CancellationToken)).ExitCode);
            JsonNode[] records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Single(records, item => ndjson ? item["event"]?.ToString() is "completed" or "failed" : item["status"] is not null);
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    // Appending windows to a session the user already owns must not move
    // them off the window they were looking at -- that is a session load
    // builds fresh, not one it is borrowing. An appended window that asks
    // for focus is the one exception.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_moves_the_client_only_when_a_window_requests_focus(bool focus)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "append-focus.socket");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = _root };
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null", ChildEnvironment = environment });
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin", "-P", "-F", "#{pane_id}");
            environment["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0");
            environment["TMUX_PANE"] = pane;
            string file = Path.Combine(_root, "append-focus.yaml");
            await File.WriteAllTextAsync(file, "session_name: irrelevant\nwindows: [{window_name: extra1, panes: [null]}, {window_name: extra2, focus: " + (focus ? "true" : "false") + ", panes: [null]}]", token);
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "--append", "--json"], output, error, _root, environment, token);

            Assert.Equal(0, code);
            Assert.Empty(error.ToString());
            JsonNode result = JsonNode.Parse(output.ToString())!["results"]![0]!;
            Assert.Equal("appended", result["status"]!.ToString());
            Assert.True(result["reused"]!.GetValue<bool>());
            Assert.Equal(focus ? "extra2" : "homewin", await Execute(server, "display-message", "-p", "-t", "home:", "#{window_name}"));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // An appended window keeps the index its document names, and a taken
    // index fails the load rather than landing the window elsewhere.
    [Theory]
    [InlineData(5, true)]
    [InlineData(0, false)]
    public async Task Append_honours_window_index(int index, bool succeeds)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "append-index.socket");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = _root };
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null", ChildEnvironment = environment });
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin", "-P", "-F", "#{pane_id}");
            environment["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0");
            environment["TMUX_PANE"] = pane;
            string file = Path.Combine(_root, "append-index.yaml");
            await File.WriteAllTextAsync(file, $"session_name: irrelevant\nwindows: [{{window_name: extra, window_index: {index}, panes: [null]}}]", token);
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "--append", "--json"], output, error, _root, environment, token);

            if (succeeds)
            {
                Assert.Equal(0, code);
                JsonNode result = JsonNode.Parse(output.ToString())!["results"]![0]!;
                Assert.Equal("appended", result["status"]!.ToString());
                Assert.Equal("extra", await Execute(server, "display-message", "-p", "-t", $"home:{index}", "#{window_name}"));
            }
            else
            {
                Assert.Equal(1, code);
                JsonNode issue = Assert.Single(JsonNode.Parse(output.ToString())!["errors"]!.AsArray())!;
                Assert.Equal("tmux_failed", issue["code"]!.ToString());
                Assert.Contains("index", issue["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal("homewin", await Execute(server, "display-message", "-p", "-t", "home:", "#{window_name}"));
                Assert.Equal("1", await Execute(server, "display-message", "-p", "#{session_windows}"));
            }
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // The human summary names what happened to the session, not what an
    // ordinary load does.
    [Fact]
    public async Task Human_summary_says_appended_after_append()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "append-message.socket");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = _root };
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null", ChildEnvironment = environment });
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin", "-P", "-F", "#{pane_id}");
            environment["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0");
            environment["TMUX_PANE"] = pane;
            string file = Path.Combine(_root, "append-message.yaml");
            await File.WriteAllTextAsync(file, "session_name: irrelevant\nwindows: [{window_name: extra, panes: [null]}]", token);
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "--append"], output, error, _root, environment, token);

            Assert.Equal(0, code);
            string text = output.ToString();
            Assert.Contains("Appended", text, StringComparison.Ordinal);
            Assert.Contains("home", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Loaded", text, StringComparison.Ordinal);
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    // schema_version identifies the --json record, not this prose; the human
    // report leads with the port instead.
    [Fact]
    public async Task Human_debug_info_leads_with_the_port_not_the_schema()
    {
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await CliRunner.RunAsync(["debug-info"], output, error, _root, environment, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        string text = output.ToString();
        Assert.DoesNotContain("schema_version", text, StringComparison.Ordinal);
        Assert.StartsWith("port: ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inherited")]
    [InlineData("same-path")]
    [InlineData("other-path")]
    [InlineData("other-name")]
    [InlineData("other-name-unstarted")]
    [InlineData("restarted")]
    public async Task Append_authenticates_the_current_panes_server(string selection)
    {
        string socket = Path.Combine(_root, "current,with,commas");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = _root };
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        Server current = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null", ChildEnvironment = environment });
        Server other = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketName = "other", ConfigurationFile = "/dev/null", ChildEnvironment = environment });
        try
        {
            string pane = await Execute(current, "new-session", "-d", "-s", "current", "-P", "-F", "#{pane_id}");
            // The refusal must be identical whether the target server is
            // already running or has never been started.
            bool otherRunning = selection != "other-name-unstarted";
            if (otherRunning) Assert.Equal(pane, await Execute(other, "new-session", "-d", "-s", "other", "-P", "-F", "#{pane_id}"));
            environment["TMUX"] = await Execute(current, "display-message", "-p", "#{socket_path},#{pid},0");
            environment["TMUX_PANE"] = pane;
            if (selection == "restarted")
            {
                using System.Diagnostics.Process original = System.Diagnostics.Process.GetProcessById(
                    int.Parse(await Execute(current, "display-message", "-p", "#{pid}"), System.Globalization.CultureInfo.InvariantCulture));
                await current.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
                using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                stopped.CancelAfter(TimeSpan.FromSeconds(5));
                await original.WaitForExitAsync(stopped.Token);
                Assert.Equal(pane, await Execute(current, "new-session", "-d", "-s", "replacement", "-P", "-F", "#{pane_id}"));
                Assert.NotEqual(environment["TMUX"], await Execute(current, "display-message", "-p", "#{socket_path},#{pid},0"));
            }
            string file = Path.Combine(_root, "append.yaml");
            await File.WriteAllTextAsync(file, "session_name: append\nwindows: [{window_name: added, panes: [null]}]", TestContext.Current.CancellationToken);
            string[] endpoint = selection switch
            {
                "same-path" => ["-S", socket],
                "other-path" => ["-S", await Execute(other, "display-message", "-p", "#{socket_path}")],
                "other-name" or "other-name-unstarted" => ["-L", "other"],
                _ => [],
            };
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "--append", "--json", .. endpoint], output, error, _root, environment, TestContext.Current.CancellationToken);
            bool mismatch = selection.StartsWith("other", StringComparison.Ordinal) || selection == "restarted";
            Assert.Equal(selection == "restarted" ? 1 : mismatch ? 2 : 0, code);
            if (mismatch)
            {
                Assert.Empty(output.ToString());
                Assert.Equal(selection == "restarted" ? "stale_environment" : "usage", JsonNode.Parse(error.ToString())!["code"]!.ToString());
            }
            else
            {
                JsonNode result = JsonNode.Parse(output.ToString())!["results"]![0]!;
                Assert.Equal("appended", result["status"]!.ToString());
                Assert.Equal("current", result["session_name"]!.ToString());
            }
            Assert.Equal(mismatch ? "1" : "2", await Execute(current, "display-message", "-p", "#{session_windows}"));
            if (otherRunning) Assert.Equal("1", await Execute(other, "display-message", "-p", "#{session_windows}"));
        }
        finally
        {
            if (await current.IsAliveAsync(TestContext.Current.CancellationToken)) await current.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
            if (await other.IsAliveAsync(TestContext.Current.CancellationToken)) await other.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    // A run-shell key binding sets TMUX but never TMUX_PANE, so an attached
    // load must not refuse for lack of a terminal or a specific pane. It
    // still reaches the switch itself -- which fails here only because no
    // client is attached to observe it, not because the load refused.
    [Fact]
    public async Task Switch_without_an_invoking_pane_does_not_require_a_terminal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "runshell.socket");
        string tmux = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin");
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = null,
            };
            string file = Path.Combine(_root, "runshell.yaml");
            await File.WriteAllTextAsync(file, "session_name: ctxw\nwindows: [{window_name: w1, panes: [null]}]", token);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "--yes", "-S", socket], output, error, _root, environment, token);
            Assert.Equal(1, code);
            Assert.Contains("switch", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("terminal", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("ctxw\nhome", await Execute(server, "list-sessions", "-F", "#{session_name}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    // --append still needs a current pane even when TMUX is set; refused as a
    // usage error rather than the earlier session_required/exit-1 shape.
    [Fact]
    public async Task Append_without_an_invoking_pane_is_a_usage_error()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "append-nopane.socket");
        string tmux = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin");
            string file = Path.Combine(_root, "append-nopane.yaml");
            await File.WriteAllTextAsync(file, "session_name: ctxw\nwindows: [{window_name: w1, panes: [null]}]", token);
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = null,
            };
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "--append", "-S", socket, "--json"], output, error, _root, environment, token);
            Assert.Equal(2, code);
            Assert.Equal("usage", JsonNode.Parse(error.ToString())!["code"]!.ToString());
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    // tmuxp checks -d first: -d --append builds a new detached session and
    // ignores --append, inside tmux and outside it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detached_beats_append(bool insideTmux)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "detached-append.socket");
        string tmux = Context(TextWriter.Null).Executable(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            string pane = await Execute(server, "new-session", "-d", "-s", "home", "-n", "homewin", "-P", "-F", "#{pane_id}");
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal);
            if (insideTmux)
            {
                environment["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0");
                environment["TMUX_PANE"] = pane;
            }
            string file = Path.Combine(_root, "detached-append.yaml");
            await File.WriteAllTextAsync(file, "session_name: ctxw\nwindows: [{window_name: w1, panes: [null]}]", token);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(["load", file, "-d", "--append", "-S", socket, "--json"], output, error, _root, environment, token);
            Assert.Equal(0, code);
            JsonNode result = JsonNode.Parse(output.ToString())!["results"]![0]!;
            Assert.Equal("created", result["status"]!.ToString());
            Assert.Equal("1", await Execute(server, "display-message", "-p", "-t", "home:", "#{session_windows}"));
            Assert.Equal("1", await Execute(server, "display-message", "-p", "-t", "ctxw:", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ConfigurationFile = "/dev/null" });
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
            Server observed = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = socket, ChildEnvironment = environment });
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
        Server current = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = currentSocket, ConfigurationFile = "/dev/null" });
        Server other = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = tmux, SocketPath = otherSocket, ConfigurationFile = "/dev/null" });
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
            JsonArray completedResults = summary["results"]!.AsArray();
            Assert.Equal(2, completedResults.Count);
            Assert.Equal("appended", completedResults[0]!["status"]!.ToString());
            Assert.Equal("failed", completedResults[1]!["status"]!.ToString());
            Assert.Equal(1, completedResults[1]!["input_index"]!.GetValue<int>());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal("stale_server", issue["code"]!.ToString());
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
            Assert.Equal("unsupported_append_extensions", JsonNode.Parse(error.ToString())!["code"]!.ToString());
            Assert.Equal("1", await Execute(server, "display-message", "-p", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    // Re-running a document that cannot load is the natural response to a
    // failure. It has to fail the same way twice rather than find the wreck
    // of the first attempt and call it a reused session.
    [Fact]
    public async Task Reloading_a_document_that_failed_fails_again_instead_of_reporting_success()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "rerun.socket");
        string file = Path.Combine(_root, "rerun.yaml");
        await File.WriteAllTextAsync(file, "session_name: rerun\nwindows:\n- {window_name: one, panes: [null]}\n- {window_name: two, options: {not-a-real-option: 1}, panes: [null]}\n- {window_name: three, panes: [null]}\n", token);
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var result = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--json");
                JsonNode summary = JsonNode.Parse(result.Output)!;
                Assert.True(result.Code == 1 && summary["status"]!.ToString() == "error", $"Attempt {attempt}: exit {result.Code}, {result.Output}");
                Assert.True(summary["errors"]![0]!["removed"]!.GetValue<bool>(), $"Attempt {attempt}: {result.Output}");
                Assert.Equal("failed", summary["results"]![0]!["status"]!.ToString());
                TmuxCommandResult listed = await server.ExecuteCommandAsync(["has-session", "-t", "=rerun"], token);
                Assert.NotEqual(0, listed.ExitCode);
            }
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    // A session found rather than created is never rebuilt, so reuse is only
    // honest when it already holds every window the document declares.
    [Fact]
    public async Task Reuse_refuses_a_session_missing_a_declared_window()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "reuse.socket");
        string file = Path.Combine(_root, "reuse.yaml");
        await File.WriteAllTextAsync(file, "session_name: reuse\nwindows: [{window_name: one, panes: [null]}, {window_name: two, panes: [null]}]\n", token);
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            await Execute(server, "new-session", "-d", "-s", "reuse", "-n", "one");
            var refused = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--json");
            JsonNode summary = JsonNode.Parse(refused.Output)!;
            Assert.True(refused.Code == 1 && summary["status"]!.ToString() == "partial", $"Exit {refused.Code}, {refused.Output}");
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal("destination_exists", issue["code"]!.ToString());
            Assert.Contains("two", issue["message"]!.ToString(), StringComparison.Ordinal);
            Assert.False(issue["removed"]!.GetValue<bool>());
            Assert.Equal("one", await Execute(server, "list-windows", "-t", "reuse", "-F", "#{window_name}"));
            await Execute(server, "new-window", "-d", "-t", "reuse", "-n", "two");
            var accepted = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.True(accepted.Code == 0, accepted.Output + accepted.Error);
            Assert.Equal("reused", JsonNode.Parse(accepted.Output)!["results"]![0]!["status"]!.ToString());
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    private static async Task<string> Execute(Server server, params string[] arguments)
    {
        TmuxCommandResult result = await server.ExecuteCommandAsync(arguments, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"tmux {string.Join(' ', arguments)} exited {result.ExitCode}: {System.Text.Encoding.UTF8.GetString(result.StandardError.Span)}");
        return System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span).TrimEnd('\n');
    }

    // Every load --json result record carries a reused boolean alongside
    // status: false when load created the session, true when it found and
    // reused (or appended to) one that already existed.
    [Fact]
    public async Task Result_records_carry_a_reused_boolean()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "reused-flag.socket");
        string file = Path.Combine(_root, "reused-flag.yaml");
        await File.WriteAllTextAsync(file, "session_name: reused-flag\nwindows: [{panes: [null]}]", token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            using StringWriter createdOutput = new();
            using StringWriter createdError = new();
            int createdCode = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], createdOutput, createdError, _root, null, token);
            using StringWriter reusedOutput = new();
            using StringWriter reusedError = new();
            int reusedCode = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"], reusedOutput, reusedError, _root, null, token);

            Assert.Equal(0, createdCode);
            Assert.Equal(0, reusedCode);
            JsonNode created = JsonNode.Parse(createdOutput.ToString())!["results"]![0]!;
            JsonNode reused = JsonNode.Parse(reusedOutput.ToString())!["results"]![0]!;
            Assert.Equal("created", created["status"]!.ToString());
            Assert.False(created["reused"]!.GetValue<bool>());
            Assert.Equal("reused", reused["status"]!.ToString());
            Assert.True(reused["reused"]!.GetValue<bool>());
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    [Theory]
    [InlineData("reuse")]
    [InlineData("freeze")]
    public async Task Session_lookup_requires_the_exact_name(string command)
    {
        string socket = Path.Combine(_root, "exact.socket");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
                Assert.Equal("session_not_found", JsonNode.Parse(result.Error)!["code"]!.ToString());
            }
            Assert.Equal("1", await Execute(server, "display-message", "-p", "-t", "=existing-long:", "#{session_windows}"));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("escape")]
    public async Task Frozen_capture_writes_only_where_save_to_names(string session)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string working = Path.Combine(_root, "working");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(outside);
        string name = session == "captured" ? "captured" : Path.Combine(outside, "captured");
        string socket = Path.Combine(_root, "destination.socket");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX"] = null, ["TMUX_PANE"] = null };
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", name);
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["freeze", "-S", socket, "--yes"], output, error, working, environment, token);

            Assert.Equal(2, code);
            Assert.Contains("--save-to", error.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(outside));
            Assert.Empty(Directory.GetFiles(working));

            string asked = Path.Combine(working, "asked.yaml");
            using StringWriter saved = new();
            using StringWriter savedError = new();
            code = await CliRunner.RunAsync(["freeze", "-S", socket, "--yes", "--save-to", asked], saved, savedError, working, environment, token);

            Assert.Equal(0, code);
            Assert.Equal([asked], Directory.GetFiles(working));
            Assert.Contains("session_name", await File.ReadAllTextAsync(asked, token), StringComparison.Ordinal);
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    // --save-to is itself consent; requiring --yes too blocks freeze in
    // anything without a terminal.
    [Fact]
    public async Task Explicit_save_to_needs_no_confirmation_flag()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string working = Path.Combine(_root, "explicit-save-to");
        Directory.CreateDirectory(working);
        string socket = Path.Combine(_root, "explicit.socket");
        Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal) { ["TMUX"] = null, ["TMUX_PANE"] = null };
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(server, "new-session", "-d", "-s", "explicit");
            string destination = Path.Combine(working, "explicit.yaml");

            using StringWriter firstOutput = new();
            using StringWriter firstError = new();
            int firstCode = await CliRunner.RunAsync(["freeze", "-S", socket, "--save-to", destination], firstOutput, firstError, working, environment, token);
            Assert.Equal(0, firstCode);
            Assert.Contains("session_name", await File.ReadAllTextAsync(destination, token), StringComparison.Ordinal);

            // A second run against the same path needs --force for the
            // pre-existing file, same as ever, but still no --yes.
            using StringWriter secondOutput = new();
            using StringWriter secondError = new();
            int secondCode = await CliRunner.RunAsync(["freeze", "-S", socket, "--save-to", destination, "--force"], secondOutput, secondError, working, environment, token);
            Assert.Equal(0, secondCode);
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("other")]
    public async Task Frozen_pane_environment_selects_only_its_own_server(string endpoint)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string inherited = Path.Combine(_root, "inherited.socket");
        string selected = Path.Combine(_root, "selected.socket");
        Server inheritedServer = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = inherited, ConfigurationFile = "/dev/null" });
        Server selectedServer = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = selected, ConfigurationFile = "/dev/null" });
        try
        {
            await Execute(inheritedServer, "new-session", "-d", "-s", "inherited-first");
            await Execute(inheritedServer, "new-session", "-d", "-s", "inherited-second");
            await Execute(selectedServer, "new-session", "-d", "-s", "selected-first");
            await Execute(selectedServer, "new-session", "-d", "-s", "selected-second");
            Dictionary<string, string?> environment = new(Context(TextWriter.Null).Environment, StringComparer.Ordinal)
            {
                ["TMUX"] = await Execute(inheritedServer, "display-message", "-p", "#{socket_path},#{pid},0"),
                ["TMUX_PANE"] = await Execute(inheritedServer, "display-message", "-p", "-t", "=inherited-second:", "#{pane_id}"),
            };
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["freeze", "-S", endpoint == "same" ? inherited : selected, "--json"], output, error, _root, environment, token);

            if (endpoint == "same")
            {
                Assert.Equal(0, code);
                Assert.Equal("inherited-second", JsonNode.Parse(output.ToString())!["session_name"]!.ToString());
            }
            else
            {
                Assert.Empty(output.ToString());
                Assert.Equal("input_required", JsonNode.Parse(error.ToString())!["code"]!.ToString());
                Assert.Equal(1, code);
            }
        }
        finally
        {
            if (await inheritedServer.IsAliveAsync(token)) await inheritedServer.KillAsync(cancellationToken: token);
            if (await selectedServer.IsAliveAsync(token)) await selectedServer.KillAsync(cancellationToken: token);
        }
    }

    [Theory]
    [InlineData("script")]
    [InlineData("options")]
    [InlineData("cancel")]
    [InlineData("next-input")]
    public async Task Partial_load_reports_completed_inputs_and_owned_session_state(string failure)
    {
        string socket = Path.Combine(_root, "partial.socket");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
            JsonNode summary = events[^1];
            Assert.Equal("partial", summary["status"]!.ToString());
            JsonArray completedResults = summary["results"]!.AsArray();
            Assert.Equal(2, completedResults.Count);
            Assert.Equal("complete", completedResults[0]!["session_name"]!.ToString());
            Assert.Equal("failed", completedResults[1]!["session_name"]!.ToString());
            Assert.Equal("failed", completedResults[1]!["status"]!.ToString());
            Assert.Equal(1, completedResults[1]!["input_index"]!.GetValue<int>());
            JsonNode issue = Assert.Single(summary["errors"]!.AsArray())!;
            Assert.Equal(1, issue["input_index"]!.GetValue<int>());
            Assert.Equal(failure != "next-input", issue["created"]!.GetValue<bool>());
            Assert.Equal(failure != "next-input", issue["removed"]!.GetValue<bool>());
            Assert.Equal(failure switch { "options" => "session-options", "next-input" => "workspace-started", _ => "before-script" }, issue["failed_stage"]!.ToString());
            Assert.Equal(failure == "next-input" ? null : "session-created", issue["completed_stage"]?.ToString());
            string[] expected = ["complete", "keeper"];
            Assert.Equal(expected, (await Execute(server, "list-sessions", "-F", "#{session_name}")).Split('\n').Order(StringComparer.Ordinal));
        }
        finally { if (await server.IsAliveAsync(TestContext.Current.CancellationToken)) await server.KillAsync(cancellationToken: TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Multi_input_session_override_changes_only_the_final_workspace()
    {
        string socket = Path.Combine(_root, "override.socket");
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = binary, SocketPath = socket, ConfigurationFile = "/dev/null" });
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
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = binary, SocketPath = socket, ConfigurationFile = "/dev/null" });
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

    // Each refusal keeps failing; only the message changes to the real
    // reason -- see WorkspacePlan.cs.
    [Theory]
    [InlineData("   ", "session_name must contain a non-whitespace character.")]
    [InlineData("a:b", "session_name must not contain ':' or '.', which tmux uses as target separators.")]
    [InlineData("a.b", "session_name must not contain ':' or '.', which tmux uses as target separators.")]
    public async Task Invalid_session_names_report_what_is_actually_wrong(string name, string expectedMessage)
    {
        string file = Path.Combine(_root, "invalid-name-" + name.GetHashCode(StringComparison.Ordinal) + ".yaml");
        await File.WriteAllTextAsync(file, "session_name: \"" + name + "\"\nwindows: [{panes: [null]}]\n", TestContext.Current.CancellationToken);
        var result = await Run("load", file, "-d", "--json");
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        JsonNode error = JsonNode.Parse(result.Error)!;
        Assert.Equal("invalid_workspace", error["code"]!.ToString());
        Assert.Equal(expectedMessage, error["message"]!.ToString());
        Assert.DoesNotContain("cannot preserve", error["message"]!.ToString(), StringComparison.Ordinal);
    }

    private CliContext Context(TextWriter output) => new(output, TextWriter.Null, _root, System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal), TestContext.Current.CancellationToken);

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync(args, output, error, _root, cancellationToken: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    // Needs a real pty -- a StringWriter can't produce
    // Console.IsOutputRedirected == false. ESC[?1h/ESC= keypad-mode escapes
    // are excluded below: runtime behaviour on any pty, not colour.
    [Fact]
    public async Task Version_on_a_real_terminal_writes_no_colour_under_color_never()
    {
        string rendered = await RunCliUnderPtyAsync(["--version", "--color", "never"], TestContext.Current.CancellationToken);
        Assert.Contains("tmux-workspace", rendered, StringComparison.Ordinal);
        Assert.False(ContainsSgrEscape(rendered), $"Expected no SGR (colour) escape codes, got: {rendered}");
    }

    // Console on Unix enables application cursor-key and keypad mode
    // (DECCKM, DECKPAM) the moment any Console member is touched, with no
    // matching reset -- reproduced with a program using a raw libc write()
    // instead of System.Console, which stays clean. Every command restores it.
    [Fact]
    public async Task Version_on_a_real_terminal_restores_cursor_and_keypad_mode()
    {
        string rendered = await RunCliUnderPtyAsync(["--version", "--color", "never"], TestContext.Current.CancellationToken);
        AssertModeRestored(rendered, "[?1h", "[?1l");
        AssertModeRestored(rendered, "=", ">");
    }

    private static void AssertModeRestored(string text, string set, string reset)
    {
        int lastSet = text.LastIndexOf(set, StringComparison.Ordinal);
        // Whether the runtime arms these depends on the pty the harness is given.
        // The property is that none is left set, so a run that armed none passes.
        if (lastSet < 0) return;
        int lastReset = text.LastIndexOf(reset, StringComparison.Ordinal);
        Assert.True(lastReset > lastSet, $"Expected {reset} after the last {set}, in: {text}");
    }

    // SGR is CSI + digits/semicolons + 'm'; excludes ESC[2K/ESC[1A (progress
    // redraw) and ESC[?1h (keypad mode), neither of which is colour.
    private static bool ContainsSgrEscape(string text)
    {
        for (int index = text.IndexOf("[", StringComparison.Ordinal); index >= 0; index = text.IndexOf("[", index + 1, StringComparison.Ordinal))
        {
            int cursor = index + 2;
            while (cursor < text.Length && (char.IsAsciiDigit(text[cursor]) || text[cursor] == ';')) cursor++;
            if (cursor < text.Length && text[cursor] == 'm') return true;
        }
        return false;
    }

    private static async Task<string> RunCliUnderPtyAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        StringBuilder command = new(PtyShellQuote("dotnet"));
        command.Append(' ').Append(PtyShellQuote(WorkspaceCliAssemblyPath()));
        foreach (string argument in arguments) command.Append(' ').Append(PtyShellQuote(argument));
        ProcessStartInfo startInfo = new("/usr/bin/script")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command.ToString());
        startInfo.ArgumentList.Add("/dev/null");
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The PTY launcher did not start.");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        await process.WaitForExitAsync(linked.Token);
        return await stdout.WaitAsync(linked.Token);
    }

    private static string PtyShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string WorkspaceCliAssemblyPath()
    {
        DirectoryInfo frameworkDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo configurationDirectory = frameworkDirectory.Parent ?? throw new InvalidOperationException("The test output has no configuration directory.");
        DirectoryInfo repositoryRoot = configurationDirectory.Parent?.Parent?.Parent?.Parent ?? throw new InvalidOperationException("The test output is outside the repository.");
        string path = Path.Combine(repositoryRoot.FullName, "src", "LibTmux.Workspace.Cli", "bin", configurationDirectory.Name, frameworkDirectory.Name, "LibTmux.Workspace.Cli.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("The workspace CLI was not built.", path);
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

    private sealed class ChannelWriter : StringWriter
    {
        private bool _failed;
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Discarded { get; private set; }
        public override async Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            if (!_failed && value.Span.Contains("\"stream\":\"stderr\"", StringComparison.Ordinal))
            {
                _failed = true;
                throw new IOException("reader closed");
            }
            if (!_failed)
            {
                await base.WriteLineAsync(value, cancellationToken);
                return;
            }
            Blocked.TrySetResult();
            try { await Release.Task; }
            catch { Discarded = true; throw; }
        }
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
