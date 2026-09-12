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
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            var result = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson");
            Assert.True(result.Code == 0, result.Error + result.Output);
            JsonNode[] events = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.True(Array.FindIndex(events, item => item["event"]!.ToString() == "session-created") < Array.FindIndex(events, item => item["event"]!.ToString() == "script-output"));
            Assert.Single(events, item => item["event"]!.ToString() == "completed");
            string sessionId = events.Single(item => item["event"]!.ToString() == "session-created")["data"]!["session_id"]!.ToString();
            TmuxCommandResult pane = await server.ExecuteCommandAsync(["display-message", "-p", "-t", sessionId + ":", "#{pane_current_path}"], TestContext.Current.CancellationToken);
            Assert.Equal(0, pane.ExitCode);
            Assert.Equal(expectedDirectory, System.Text.Encoding.UTF8.GetString(pane.StandardOutput.Span).TrimEnd('\n'));
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
                ["TMUX"] = await Execute(server, "display-message", "-p", "#{socket_path},#{pid},0"), ["TMUX_PANE"] = pane,
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

    private sealed class CancellingWriter(CancellationTokenSource? cancellation, string eventName = "script-output") : StringWriter
    {
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
    }

    public void Dispose() => Directory.Delete(_root, true);
}
