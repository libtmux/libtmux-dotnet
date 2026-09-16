using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-live-" + Guid.NewGuid().ToString("N"));

    public ExecutionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(2, "none")]
    [InlineData(3, "none")]
    [InlineData(4, "none")]
    [InlineData(3, "before")]
    [InlineData(3, "after")]
    public async Task Native_load_preserves_pane_creation_order(int count, string synchronization)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "tmux");
        string file = Path.Combine(_root, "ordered.json");
        JsonArray panes = [];
        for (int index = 0; index < count; index++) panes.Add(new JsonObject
        {
            ["shell_command"] = $"printf {(char)('A' + index)} >> '{_root}'/\"$TMUX_PANE\"",
            ["focus"] = index == 1,
        });
        JsonObject options = new() { ["pane-base-index"] = 7 };
        if (synchronization == "before") options["synchronize-panes"] = true;
        JsonObject window = new() { ["window_name"] = "main", ["layout"] = "even-horizontal", ["options"] = options, ["panes"] = panes };
        if (synchronization == "after") window["options_after"] = new JsonObject { ["synchronize-panes"] = true };
        JsonObject document = new() { ["session_name"] = "ordered", ["windows"] = new JsonArray(window) };
        await File.WriteAllTextAsync(file, document.ToJsonString(), token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        async Task<string> Native(params string[] arguments)
        {
            TmuxCommandResult result = await server.ExecuteCommandAsync(arguments, token);
            Assert.Equal(0, result.ExitCode);
            return System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span).TrimEnd('\n');
        }
        try
        {
            await Native("new-session", "-d", "-s", "keeper", "/bin/sleep 120");
            string keeper = await Native("list-panes", "-t", "keeper", "-F", "#{pid}|#{session_id}|#{window_id}|#{pane_id}|#{pane_pid}");
            Assert.NotEmpty(keeper);
            (int code, string output, string error) = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson");
            Assert.Empty(error);
            Assert.Equal(0, code);
            string[] created = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).Where(record => record["event"]?.ToString() == "pane-created").Select(record => record["pane_id"]!.ToString()).ToArray();
            Assert.Equal(count, created.Length);
            for (int index = 0; index < count; index++)
            {
                string expected = synchronization == "before" ? new string(Enumerable.Range(index, count - index).Select(value => (char)('A' + value)).ToArray()) : ((char)('A' + index)).ToString();
                string marker = Path.Combine(_root, created[index]);
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (File.Exists(marker) && await File.ReadAllTextAsync(marker, token) == expected) break;
                    await Task.Delay(20, token);
                }
                Assert.Equal(expected, await File.ReadAllTextAsync(marker, token));
            }
            Assert.Equal(created[1], await Native("display-message", "-p", "-t", "ordered:main", "#{pane_id}"));
            Assert.Equal(synchronization == "none" ? "off" : "on", await Native("show-options", "-wAv", "-t", "ordered:main", "synchronize-panes"));
            Assert.Equal(keeper, await Native("list-panes", "-t", "keeper", "-F", "#{pid}|#{session_id}|#{window_id}|#{pane_id}|#{pane_pid}"));
            string[][] actual = (await Native("list-panes", "-t", "ordered:main", "-F", "#{pane_id}|#{pane_index}|#{pane_left}|#{pane_top}|#{pane_width}|#{window_width}")).Split('\n').Select(line => line.Split('|')).ToArray();
            Assert.Equal(created, actual.Select(fields => fields[0]));
            int left = 0;
            for (int index = 0; index < count; index++)
            {
                int[] fields = actual[index][1..].Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Assert.Equal(index + 7, fields[0]);
                Assert.Equal(left, fields[1]);
                Assert.Equal(0, fields[2]);
                Assert.True(fields[3] > 0);
                left += fields[3] + 1;
                if (index == count - 1) Assert.Equal(fields[4], left - 1);
            }
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Fact]
    public async Task Native_load_and_capture_preserve_index_directory_environment_and_focus()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "tmux");
        string marker = Path.Combine(_root, "environment");
        string file = Path.Combine(_root, "workspace.yaml");
        await File.WriteAllTextAsync(file, $$"""
            session_name: native
            start_directory: /tmp
            environment: {CLI_VALUE: session}
            options: {base-index: 0}
            windows:
              - window_name: editor
                window_index: 0
                focus: true
                environment: {CLI_VALUE: window}
                options: {automatic-rename: false}
                layout: even-horizontal
                panes:
                  - start_directory: /
                    environment: {CLI_VALUE: pane}
                    shell_command:
                      - cmd: printf %s "$CLI_VALUE" > '{{marker}}'
                  - focus: true
                    shell_command: [blank]
              - window_name: shell
                window_index: 4
                panes: [["printf ready"]]
            """, token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            (int code, string output, string error) = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.Equal("", error);
            Assert.Equal(0, code);
            Assert.Equal("ok", JsonNode.Parse(output)!["status"]!.ToString());
            for (int attempt = 0; attempt < 100 && !File.Exists(marker); attempt++) await Task.Delay(20, token);
            try { Assert.Equal("pane", await File.ReadAllTextAsync(marker, token)); }
            catch (FileNotFoundException failure)
            {
                throw new FileNotFoundException(failure.Message + "\n" + await MarkerDiagnostics(server, marker, token), failure.FileName, failure);
            }
            (code, output, error) = await Run("freeze", "native", "-S", socket, "--json");
            Assert.Equal("capture-lossy", JsonNode.Parse(error)!["code"]!.ToString());
            Assert.Equal(0, code);
            JsonNode capture = JsonNode.Parse(output)!;
            Assert.Equal("native", capture["session_name"]!.ToString());
            JsonArray windows = capture["windows"]!.AsArray();
            Assert.Equal(2, windows.Count);
            Assert.Equal("off", windows[0]!["options"]!["automatic-rename"]!.ToString());
            Assert.Equal(0, windows[0]!["window_index"]!.GetValue<int>());
            Assert.Equal(4, windows[1]!["window_index"]!.GetValue<int>());
            Assert.Equal("/", windows[0]!["panes"]![0]!["start_directory"]!.ToString());
            Assert.True(windows[0]!["focus"]!.GetValue<bool>());
            Assert.True(windows[0]!["panes"]![1]!["focus"]!.GetValue<bool>());
            (code, output, error) = await Run("--log-level", "error", "freeze", "native", "-S", socket, "--json");
            Assert.Equal(0, code);
            Assert.Empty(error);
            Assert.Equal("native", JsonNode.Parse(output)!["session_name"]!.ToString());
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Fact]
    public async Task Import_transforms_native_documents_without_a_python_runtime()
    {
        string file = Path.Combine(_root, "project.yaml");
        await File.WriteAllTextAsync(file, "name: project\nroot: /tmp\nwindows:\n  - editor: [echo hello, null]\n", TestContext.Current.CancellationToken);
        (int code, string output, string error) = await Run("import", "tmuxinator", file, "--json");
        Assert.Empty(error);
        Assert.Equal(0, code);
        JsonNode result = JsonNode.Parse(output)!;
        Assert.Equal("project", result["session_name"]!.ToString());
        Assert.Equal("editor", result["windows"]![0]!["window_name"]!.ToString());
        Assert.Single(result["windows"]![0]!["panes"]!.AsArray());
        Assert.Equal("echo hello", result["windows"]![0]!["panes"]![0]!["shell_command"]![0]!.ToString());
    }

    [Fact]
    public async Task Imported_teamocil_preserves_commands_options_and_first_focus()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string source = Path.Combine(_root, "teamocil.yaml");
        string destination = Path.Combine(_root, "imported.json");
        string marker = Path.Combine(_root, "import-marker");
        string socket = Path.Combine(_root, "tmux");
        await File.WriteAllTextAsync(source, $$"""
            session:
              name: imported
              windows:
                - name: original
                  root: {{_root}}
                  focus: true
                  layout: even-horizontal
                  options: {automatic-rename: false}
                  panes:
                    - commands:
                        - IMPORT_TEST_VALUE=preserved
                        - printf %s "$IMPORT_TEST_VALUE" > '{{marker}}'
                    - commands: []
                      focus: true
                    - commands: []
                      focus: true
                - name: later
                  focus: true
                  panes: [null]
            """, token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        async Task<string> Native(params string[] arguments)
        {
            TmuxCommandResult result = await server.ExecuteCommandAsync(arguments, token);
            Assert.Equal(0, result.ExitCode);
            return System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span).TrimEnd('\n');
        }
        try
        {
            await Native("new-session", "-d", "-s", "keeper", "/bin/sleep 120");
            string keeper = await Native("list-panes", "-t", "keeper", "-F", "#{pid}|#{session_id}|#{window_id}|#{pane_id}|#{pane_pid}");
            Assert.NotEmpty(keeper);
            (int code, _, string error) = await Run("import", "teamocil", source, "--save-to", destination, "--workspace-format", "json", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            string records;
            (code, records, error) = await Run("load", destination, "-d", "-S", socket, "--ndjson");
            Assert.Empty(error);
            Assert.Equal(0, code);
            for (int attempt = 0; attempt < 100 && !File.Exists(marker); attempt++) await Task.Delay(20, token);
            Assert.Equal("preserved", await File.ReadAllTextAsync(marker, token));
            Assert.Equal("original", await Native("display-message", "-p", "-t", "imported", "#{window_name}"));
            JsonNode[] created = records.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).Where(record => record["event"]?.ToString() == "pane-created").ToArray();
            Assert.Equal(4, created.Length);
            Assert.Equal(created[1]["pane_id"]!.ToString(), await Native("display-message", "-p", "-t", "imported:original", "#{pane_id}"));
            Assert.Equal("off", await Native("show-options", "-w", "-v", "-t", "imported:original", "automatic-rename"));
            Assert.Equal(keeper, await Native("list-panes", "-t", "keeper", "-F", "#{pid}|#{session_id}|#{window_id}|#{pane_id}|#{pane_pid}"));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Theory]
    [InlineData("tmuxinator", "yaml", "root", false)]
    [InlineData("tmuxinator", "yaml", "root", true)]
    [InlineData("tmuxinator", "yaml", "command", false)]
    [InlineData("tmuxinator", "yaml", "command", true)]
    [InlineData("tmuxinator", "yaml", "key", false)]
    [InlineData("tmuxinator", "json", "root", false)]
    [InlineData("tmuxinator", "json", "root", true)]
    [InlineData("tmuxinator", "json", "command", false)]
    [InlineData("tmuxinator", "json", "command", true)]
    [InlineData("tmuxinator", "json", "key", false)]
    [InlineData("teamocil", "yaml", "command", false)]
    [InlineData("teamocil", "json", "command", true)]
    public async Task Tmuxinator_templates_fail_before_publication_and_teamocil_preserves_them(string kind, string format, string field, bool save)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string template = "<%= 1 + 1 %>";
        string root = field == "root" ? template : ".";
        string command = field == "command" ? template : "echo ready";
        string key = field == "key" ? template : "main";
        string content = kind == "tmuxinator"
            ? format == "json"
                ? $$"""{"name":"imported","root":"{{root}}","windows":[{"{{key}}":"{{command}}"}]}"""
                : $"name: imported\nroot: '{root}'\nwindows: [{{'{key}': '{command}'}}]\n"
            : format == "json"
                ? $$"""{"name":"imported","windows":[{"name":"main","panes":["{{command}}"]}]}"""
                : $"name: imported\nwindows: [{{name: main, panes: ['{command}']}}]\n";
        string source = Path.Combine(_root, "template." + format);
        string destination = Path.Combine(_root, "existing.json");
        await File.WriteAllTextAsync(source, content, token);
        await File.WriteAllTextAsync(destination, "retain existing document", token);

        (int converted, string document, string conversionError) = await Run("convert", source, "--json");
        Assert.Equal(0, converted);
        Assert.Empty(conversionError);
        JsonNode value = JsonNode.Parse(document)!;
        JsonObject window = value["windows"]!.AsArray()[0]!.AsObject();
        string observed = field switch
        {
            "root" => value["root"]!.ToString(),
            "key" => window.Single().Key,
            _ when kind == "tmuxinator" => window["main"]!.ToString(),
            _ => window["panes"]![0]!.ToString(),
        };
        Assert.Equal(template, observed);

        string[] saveArguments = !save ? [] : kind == "teamocil" ? ["--save-to", destination, "--force", "--workspace-format", "json"] : ["--save-to", destination, "--force"];
        string[] arguments = ["import", kind, source, "--json", .. saveArguments];
        (int code, string output, string error) = await Run(arguments);

        if (kind == "tmuxinator")
        {
            Assert.Equal(1, code);
            Assert.Empty(output);
            Assert.Equal("invalid-config", JsonNode.Parse(error)!["code"]!.ToString());
            Assert.Contains("ERB", JsonNode.Parse(error)!["message"]!.ToString(), StringComparison.Ordinal);
            Assert.Equal(content, await File.ReadAllTextAsync(source, token));
            Assert.Equal("retain existing document", await File.ReadAllTextAsync(destination, token));
        }
        else
        {
            // Teamocil evaluates no templates, so this markup must survive
            // verbatim rather than being refused like tmuxinator's.
            Assert.Empty(error);
            Assert.Equal(0, code);
            JsonNode imported = save ? JsonNode.Parse(await File.ReadAllTextAsync(destination, token))! : JsonNode.Parse(output)!;
            Assert.Equal(template, imported["windows"]![0]!["panes"]![0]!["shell_command"]!.ToString());
        }
    }

    [Theory]
    [InlineData("tmuxinator", "name: imported\npre: echo launcher\npre_window: echo pane\nwindows: [{main: echo ready}]", "pre")]
    [InlineData("teamocil", "name: imported\nwindows: [{name: main, filters: {after: echo after}, panes: [null]}]", "filters")]
    [InlineData("teamocil", "name: imported\nwindows: [{name: main, clear: true, panes: [null]}]", "clear")]
    [InlineData("tmuxinator", "name: imported\nwindows: [{main: {panes: [{title: echo ready}]}}]", "pane")]
    [InlineData("teamocil", "name: imported\nwindows: [{name: main, panes: [{commands: [42]}]}]", "commands")]
    [InlineData("teamocil", "name: imported\nwindows: [{name: main, options: {automatic-rename: []}, panes: [null]}]", "scalar")]
    [InlineData("tmuxinator", "name: imported\nproject_name: other\nwindows: [{main: null}]", "Conflicting")]
    [InlineData("tmuxinator", "name: imported\nwindows: [{main: {pre: echo ignored}}]", "pre")]
    [InlineData("tmuxinator", "name: imported\nwindows: [{main: {synchronize: sometimes, panes: [null]}}]", "synchronize")]
    [InlineData("teamocil", "name: imported\nwindows: [{name: main, layout: invalid-layout, panes: [null]}]", "Layout")]
    [InlineData("teamocil", "name: imported\nwindows: []", "window")]
    public async Task Import_refuses_unrepresentable_or_invalid_source_before_saving(string kind, string content, string diagnostic)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string source = Path.Combine(_root, "source.yaml");
        string destination = Path.Combine(_root, "existing.json");
        await File.WriteAllTextAsync(source, content, token);
        await File.WriteAllTextAsync(destination, "retain existing document", token);

        (int code, string output, string error) = await Run("import", kind, source, "--save-to", destination, "--force", "--json");

        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Equal("invalid-config", JsonNode.Parse(error)!["code"]!.ToString());
        Assert.Contains(diagnostic, JsonNode.Parse(error)!["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("retain existing document", await File.ReadAllTextAsync(destination, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imported_tmuxinator_preserves_same_pane_commands_and_prefix_groups(bool split)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string marker = Path.Combine(_root, "prefix-marker");
        string source = Path.Combine(_root, "tmuxinator.json");
        string destination = Path.Combine(_root, "imported.json");
        string socket = Path.Combine(_root, "tmux");
        JsonArray commands = [$"printf %s \"$IMPORT_PREFIX_TEST\" > '{marker}'", $"printf %s :end >> '{marker}'"];
        JsonNode window = split ? new JsonObject
        {
            ["pre"] = new JsonArray("IMPORT_PREFIX_TEST=\"${IMPORT_PREFIX_TEST}:window\"", "IMPORT_PREFIX_TEST=\"${IMPORT_PREFIX_TEST}:ready\""),
            ["panes"] = new JsonArray(commands, null),
            ["synchronize"] = "after",
        } : commands;
        JsonObject document = new()
        {
            ["name"] = "prefixes",
            ["pre_window"] = new JsonArray("IMPORT_PREFIX_TEST=first", "IMPORT_PREFIX_TEST=\"${IMPORT_PREFIX_TEST}:second\""),
            ["windows"] = new JsonArray(new JsonObject { ["main"] = window }),
        };
        await File.WriteAllTextAsync(source, document.ToJsonString(), token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            (int code, _, string error) = await Run("import", "tmuxinator", source, "--save-to", destination, "--workspace-format", "json", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            (code, _, error) = await Run("load", destination, "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            for (int attempt = 0; attempt < 100 && !File.Exists(marker); attempt++) await Task.Delay(20, token);
            string expected = split ? "first:second:window:ready:end" : "first:second:end";
            for (int attempt = 0; attempt < 100 && await File.ReadAllTextAsync(marker, token) != expected; attempt++) await Task.Delay(20, token);
            Assert.Equal(expected, await File.ReadAllTextAsync(marker, token));
            TmuxCommandResult panes = await server.ExecuteCommandAsync(["list-panes", "-t", "prefixes:main", "-F", "#{pane_id}"], token);
            Assert.Equal(0, panes.ExitCode);
            Assert.Equal(split ? 2 : 1, System.Text.Encoding.UTF8.GetString(panes.StandardOutput.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            if (split)
            {
                TmuxCommandResult option = await server.ExecuteCommandAsync(["show-options", "-w", "-v", "-t", "prefixes:main", "synchronize-panes"], token);
                Assert.Equal(0, option.ExitCode);
                Assert.Equal("on\n", System.Text.Encoding.UTF8.GetString(option.StandardOutput.Span));
            }
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Theory]
    [InlineData("teamocil")]
    [InlineData("tmuxinator")]
    public async Task Import_preserves_relative_roots_when_source_and_destination_move(string kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string saved = Directory.CreateDirectory(Path.Combine(_root, "saved")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "project", "child"));
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        string expected = Path.Combine(_root, kind == "teamocil" ? "child" : "project/child");
        string identity = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(expected, "directory-identity"), identity, token);
        string source = Path.Combine(sources, "inferred-name.yaml");
        string destination = Path.Combine(saved, "workspace.json");
        string socket = Path.Combine(_root, "tmux");
        await File.WriteAllTextAsync(source, kind == "teamocil"
            ? "root: project\nwindows: [{name: main, root: child, panes: [null]}]"
            : "root: project\npre: ''\nwindows: [{main: {root: child, panes: [null]}}]", token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            (int code, _, string error) = await Run("import", kind, source, "--save-to", destination, "--workspace-format", "json", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            (code, _, error) = await Run("load", destination, "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            TmuxCommandResult path = await server.ExecuteCommandAsync(["display-message", "-p", "-t", "inferred-name:main", "#{pane_current_path}"], token);
            Assert.Equal(0, path.ExitCode);
            string observed = System.Text.Encoding.UTF8.GetString(path.StandardOutput.Span).TrimEnd('\n');
            Assert.Equal(identity, await File.ReadAllTextAsync(Path.Combine(observed, "directory-identity"), token));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Theory]
    [InlineData("teamocil", "before")]
    [InlineData("tmuxinator", "before")]
    [InlineData("tmuxinator", "after")]
    public async Task Imported_synchronization_preserves_native_command_delivery(string kind, string phase)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string source = Path.Combine(_root, "sync.json");
        string destination = Path.Combine(_root, "imported.json");
        string socket = Path.Combine(_root, "tmux");
        string first = $"printf A >> '{_root}'/\"$TMUX_PANE\"";
        string second = $"printf B >> '{_root}'/\"$TMUX_PANE\"";
        JsonObject window = kind == "teamocil"
            ? new JsonObject { ["name"] = "main", ["options"] = new JsonObject { ["synchronize-panes"] = true }, ["panes"] = new JsonArray(first, second) }
            : new JsonObject { ["main"] = new JsonObject { ["synchronize"] = phase, ["panes"] = new JsonArray(first, second) } };
        JsonObject document = new() { ["name"] = "synchronized", ["windows"] = new JsonArray(window) };
        await File.WriteAllTextAsync(source, document.ToJsonString(), token);
        Server server = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            (int code, _, string error) = await Run("import", kind, source, "--save-to", destination, "--workspace-format", "json", "--json");
            Assert.Empty(error);
            Assert.Equal(0, code);
            string records;
            (code, records, error) = await Run("load", destination, "-d", "-S", socket, "-f", "/dev/null", "--ndjson");
            Assert.Empty(error);
            Assert.Equal(0, code);
            string[] panes = records.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).Where(record => record["event"]?.ToString() == "pane-created").Select(record => record["pane_id"]!.ToString()).ToArray();
            Assert.Equal(2, panes.Length);
            string[] expected = [phase == "before" ? "AB" : "A", "B"];
            for (int index = 0; index < panes.Length; index++)
            {
                string marker = Path.Combine(_root, panes[index]);
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (File.Exists(marker) && await File.ReadAllTextAsync(marker, token) == expected[index]) break;
                    await Task.Delay(20, token);
                }
                Assert.Equal(expected[index], await File.ReadAllTextAsync(marker, token));
            }
            TmuxCommandResult option = await server.ExecuteCommandAsync(["show-options", "-w", "-v", "-t", "synchronized:main", "synchronize-panes"], token);
            Assert.Equal(0, option.ExitCode);
            Assert.Equal("on\n", System.Text.Encoding.UTF8.GetString(option.StandardOutput.Span));
        }
        finally { if (await server.IsAliveAsync(token)) await server.KillAsync(cancellationToken: token); }
    }

    [Fact]
    public async Task Unsupported_configuration_fails_before_server_creation()
    {
        string socket = Path.Combine(_root, "unused");
        string file = Path.Combine(_root, "invalid.yaml");
        await File.WriteAllTextAsync(file, "session_name: invalid\nunknown_runtime_key: true\nwindows: [{panes: [null]}]", TestContext.Current.CancellationToken);
        (int code, string output, string error) = await Run("load", file, "-d", "-S", socket, "--json");
        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Equal("invalid-config", JsonNode.Parse(error)!["code"]!.ToString());
        Assert.False(File.Exists(socket));
    }

    [Theory]
    [InlineData("32d2,80x24,0,0{}", 1)]
    [InlineData("ffff,80x24,0,0,0", 1)]
    [InlineData("b25d,80x24,0,0,0", 2)]
    public async Task Later_invalid_layout_is_refused_before_backend_resolution(string layout, int paneCount)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string first = Path.Combine(_root, "first.yaml");
        string second = Path.Combine(_root, "second.json");
        await File.WriteAllTextAsync(first, "session_name: first\nbefore_script: /bin/false\nwindows: [{panes: [null]}]", token);
        JsonArray panes = [];
        for (int index = 0; index < paneCount; index++) panes.Add((JsonNode?)null);
        JsonObject document = new()
        {
            ["session_name"] = "second",
            ["windows"] = new JsonArray(new JsonObject { ["layout"] = layout, ["panes"] = panes }),
        };
        await File.WriteAllTextAsync(second, document.ToJsonString(), token);
        Dictionary<string, string?> environment = new()
        {
            ["HOME"] = _root,
            ["PATH"] = "/usr/bin:/bin",
            ["LIBTMUX_TMUX"] = Path.Combine(_root, "missing-tmux"),
        };
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await CliRunner.RunAsync(["load", first, second, "-d", "--json"],
            output, error, _root, environment, token);

        Assert.Equal(1, code);
        Assert.Empty(output.ToString());
        Assert.Equal("invalid-config", JsonNode.Parse(error.ToString())!["code"]!.ToString());
    }

    private static async Task<string> MarkerDiagnostics(Server server, string marker, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        List<string> observations = [$"Marker exists: {File.Exists(marker)}"];
        async Task<string?> Observe(string label, params string[] arguments)
        {
            try
            {
                TmuxCommandResult result = await server.ExecuteCommandAsync(arguments, timeout.Token);
                string stdout = System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span);
                string stderr = System.Text.Encoding.UTF8.GetString(result.StandardError.Span);
                observations.Add($"{label} (exit {result.ExitCode}):\n{stdout}{stderr}");
                return result.ExitCode == 0 ? stdout : null;
            }
            catch (Exception failure)
            {
                observations.Add($"{label}: {failure.GetType().Name}: {failure.Message}");
                return null;
            }
        }
        await Observe("Daemon version", "display-message", "-p", "#{version}");
        string? panes = await Observe("Panes (id, session, window, index, command, dead)",
            "list-panes", "-a", "-F", "#{pane_id}\t#{session_name}\t#{window_index}\t#{pane_index}\t#{pane_current_command}\t#{pane_dead}");
        foreach (string row in (panes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string paneId = row.Split('\t')[0];
            await Observe("Capture " + paneId, "capture-pane", "-p", "-t", paneId);
        }
        return string.Join('\n', observations);
    }

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync(args, output, error, _root, cancellationToken: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
