using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-live-" + Guid.NewGuid().ToString("N"));

    public ExecutionTests() => Directory.CreateDirectory(_root);

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
        Server server = Server.Open(new ServerConnectionOptions(socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            (int code, string output, string error) = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--json");
            Assert.Equal("", error);
            Assert.Equal(0, code);
            Assert.Equal("ok", JsonNode.Parse(output)!["status"]!.ToString());
            for (int attempt = 0; attempt < 100 && !File.Exists(marker); attempt++) await Task.Delay(20, token);
            Assert.Equal("pane", await File.ReadAllTextAsync(marker, token));
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
        Assert.Equal(2, result["windows"]![0]!["panes"]!.AsArray().Count);
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

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await CliRunner.RunAsync(args, output, error, _root, cancellationToken: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
