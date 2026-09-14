using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class LayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-layout-" + Guid.NewGuid().ToString("N"));

    public LayoutTests() => Directory.CreateDirectory(_root);

    public static TheoryData<string, string, int, bool, bool> LayoutCases
    {
        get
        {
            using Stream input = typeof(LayoutTests).Assembly.GetManifestResourceStream("LibTmux.Tests.Layouts.json")!;
            using JsonDocument corpus = JsonDocument.Parse(input);
            TheoryData<string, string, int, bool, bool> cases = [];
            foreach (JsonElement item in corpus.RootElement.EnumerateArray())
            {
                cases.Add(item.GetProperty("id").GetString()!, item.GetProperty("layout").GetString()!,
                    item.GetProperty("pane_count").GetInt32(),
                    item.GetProperty("expected_valid").GetProperty("3.3a").GetBoolean(),
                    item.GetProperty("expected_valid").GetProperty("3.7c").GetBoolean());
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public async Task Native_layout_corpus_preserves_the_keeper(
        string id, string layout, int paneCount, bool beforeMirrors, bool afterMirrors)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string socket = Path.Combine(_root, "tmux");
        string marker = Path.Combine(_root, "script-ran");
        string file = Path.Combine(_root, "workspace.json");
        JsonArray panes = [];
        for (int index = 0; index < paneCount; index++) panes.Add((JsonNode?)null);
        JsonObject document = new()
        {
            ["session_name"] = "candidate",
            ["before_script"] = "/usr/bin/touch " + marker,
            ["workspace_builder_options"] = new JsonObject { ["pane_readiness"] = "never" },
            ["windows"] = new JsonArray(new JsonObject { ["layout"] = layout, ["panes"] = panes }),
        };
        await File.WriteAllTextAsync(file, document.ToJsonString(), token);
        Server server = Server.Open(new ServerConnectionOptions { 
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketPath = socket, ConfigurationFile = "/dev/null" });
        try
        {
            Session keeper = await server.CreateSessionAsync(new NewSessionRequest("keeper", command: "/bin/sh"), token);
            server = keeper.Server;
            string before = await Keeper(server, token);
            bool expected = server.Version >= TmuxVersion.Parse("3.5") ? afterMirrors : beforeMirrors;
            bool geometry = id is "bad-inner-size" or "nested-invalid-width" or "nested-short-parent";
            using StringWriter output = new();
            using StringWriter error = new();

            int code = await CliRunner.RunAsync(["load", file, "-d", "-S", socket, "-f", "/dev/null", "--json"],
                output, error, _root, cancellationToken: token);

            Assert.Equal(expected ? 0 : 1, code);
            Assert.Equal(before, await Keeper(server, token));
            if (!expected && !geometry)
            {
                Assert.Empty(output.ToString());
                Assert.Equal("invalid-config", JsonNode.Parse(error.ToString())!["code"]!.ToString());
                Assert.False(File.Exists(marker));
                Assert.Single(await server.GetSessionsAsync(token));
            }
            else
            {
                Assert.True(File.Exists(marker));
                Assert.NotEmpty(output.ToString());
            }
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (await server.IsAliveAsync(cleanup.Token)) await server.KillAsync(cancellationToken: cleanup.Token);
        }
    }

    private static async Task<string> Keeper(Server server, CancellationToken token)
    {
        TmuxCommandResult result = await server.ExecuteCommandAsync(
            ["display-message", "-p", "-t", "=keeper:", "#{pid}:#{start_time}\t#{session_id}\t#{window_id}\t#{pane_id}\t#{window_layout}"], token);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardErrorLines);
        return Assert.Single(result.StandardOutputLines);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
