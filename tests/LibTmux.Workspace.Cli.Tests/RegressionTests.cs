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

    [Fact]
    public async Task Before_script_uses_direct_argv_session_directory_and_created_session()
    {
        string socket = Path.Combine(_root, "script.socket");
        string directory = Path.Combine(_root, "working");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(_root, "script.json");
        JsonObject document = new()
        {
            ["session_name"] = "script", ["start_directory"] = directory,
            ["before_script"] = "/bin/sh -c 'test -S \"" + socket + "\" && test \"$(pwd)\" = \"" + directory + "\" && printf ready'",
            ["windows"] = new JsonArray(new JsonObject { ["panes"] = new JsonArray((JsonNode?)null) }),
        };
        await File.WriteAllTextAsync(file, document.ToJsonString(), TestContext.Current.CancellationToken);
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", socketPath: socket, configurationFile: "/dev/null"));
        try
        {
            var result = await Run("load", file, "-d", "-S", socket, "-f", "/dev/null", "--ndjson");
            Assert.True(result.Code == 0, result.Error + result.Output);
            JsonNode[] events = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.True(Array.FindIndex(events, item => item["event"]!.ToString() == "session-created") < Array.FindIndex(events, item => item["event"]!.ToString() == "script-output"));
            Assert.Single(events, item => item["event"]!.ToString() == "completed");
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
