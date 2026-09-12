using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli.Tests;

public sealed class ContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-contract-" + Guid.NewGuid().ToString("N"));
    public ContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Search_preserves_reference_match_records_and_field_aliases()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dev.yaml"), "session_name: project\nwindows: [{window_name: editor, panes: [echo hello]}]\n", TestContext.Current.CancellationToken);
        var result = await Run("search", "s:project", "pane:hello", "--json");
        Assert.Equal(0, result.Code);
        JsonNode row = JsonNode.Parse(result.Output)![0]!;
        Assert.Equal("project", row["matches"]!["session_name"]![0]!.ToString());
        Assert.Equal("hello", row["matches"]!["pane"]![0]!.ToString());
        Assert.Equal(2, row["matched_fields"]!.AsArray().Count);
        result = await Run("search", "dev", "-f", "n", "--json");
        Assert.Equal(0, result.Code);
        Assert.Single(JsonNode.Parse(result.Output)!.AsArray());
    }

    [Fact]
    public async Task List_includes_reference_metadata_and_private_paths()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dev.yaml"), "session_name: project\nwindows: []\n", TestContext.Current.CancellationToken);
        var result = await Run("ls", "--json");
        JsonNode row = JsonNode.Parse(result.Output)!["workspaces"]![0]!;
        Assert.Equal("yaml", row["format"]!.ToString());
        Assert.True(row["size"]!.GetValue<long>() > 0);
        Assert.NotNull(row["mtime"]);
        Assert.Equal("~/dev.yaml", row["path"]!.ToString());
    }

    [Fact]
    public async Task Export_uses_command_graph_and_needs_no_tmux()
    {
        var result = await Run("--generate", "reference", "--json");
        Assert.Equal(0, result.Code);
        JsonNode root = JsonNode.Parse(result.Output)!;
        Assert.Equal("tmux-workspace", root["name"]!.ToString());
        Assert.Equal(9, root["commands"]!.AsArray().Count);
        Assert.Equal(2, root["commands"]!.AsArray().Single(command => command!["name"]!.ToString() == "import")!["commands"]!.AsArray().Count);
        JsonArray options = root["commands"]!.AsArray().Single(command => command!["name"]!.ToString() == "load")!["options"]!.AsArray();
        JsonNode lines = options.Single(option => option!["environment"]?.ToString() == "TMUXP_PROGRESS_LINES")!;
        Assert.Equal(3, lines["default"]!.GetValue<int>());
        Assert.Contains("TMUXP_PROGRESS_LINES", lines["description"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal("default", options.Single(option => option!["environment"]?.ToString() == "TMUXP_PROGRESS_FORMAT")!["default"]!.ToString());
        Assert.Contains(options, option => option!["environment"]?.ToString() == "TMUXP_PROGRESS=0");
    }

    [Theory]
    [InlineData("load", "file", "--progress-lines", "-2")]
    [InlineData("shell", "--pdb", "--ipython")]
    public async Task Invalid_values_fail_before_backend(params string[] args)
    {
        var result = await Run([.. args, "--json"]);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task Convert_requires_force_and_quiet_keeps_machine_save_result()
    {
        string input = Path.Combine(_root, "input.yaml");
        string target = Path.Combine(_root, "result.json");
        await File.WriteAllTextAsync(input, "session_name: project\nwindows: []", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);
        var result = await Run("convert", input, "--save-to", target, "-y", "--json");
        Assert.Equal(1, result.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        result = await Run("convert", input, "--save-to", target, "--force", "--json");
        Assert.Equal(0, result.Code);
        Assert.Equal(target, JsonNode.Parse(result.Output)!["destination"]!.ToString());
    }

    [Fact]
    public async Task Explicit_help_overrides_required_positionals()
    {
        var result = await Run("load", "--help", "--json");
        Assert.Equal(0, result.Code);
        Assert.Contains("workspace-file", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Installed_command_identity_and_machine_version_use_one_native_graph()
    {
        var result = await Run("--help");
        Assert.Contains("tmux-workspace [", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, result.Output.Split("--version", StringSplitOptions.None).Length - 1);
        result = await Run("--version", "--json");
        Assert.Equal("tmux-workspace", JsonNode.Parse(result.Output)!["name"]!.ToString());
        Assert.NotEmpty(JsonNode.Parse(result.Output)!["version"]!.ToString());
    }

    [Fact]
    public void Editor_tokenizer_preserves_quoted_arguments_without_shell_expansion()
    {
        string[] expected = ["editor", "a b", "$(never)", "", "c d"];
        Assert.Equal(expected, ProcessCommands.SplitArguments("editor 'a b' '$(never)' '' c\\ d"));
        Assert.Throws<CliException>(() => ProcessCommands.SplitArguments("editor 'unfinished"));
    }

    [Fact]
    public void Workspace_names_and_explicit_paths_have_distinct_resolution_rules()
    {
        string global = Path.Combine(_root, "global");
        string local = Path.Combine(_root, "project");
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(global, "project.yaml"), "session_name: global");
        File.WriteAllText(Path.Combine(global, "missing.yaml"), "session_name: global");
        File.WriteAllText(Path.Combine(local, ".tmuxp.yaml"), "session_name: local");
        File.WriteAllText(Path.Combine(_root, "project.yaml"), "session_name: explicit");
        Dictionary<string, string?> environment = new(StringComparer.Ordinal) { ["HOME"] = _root, ["TMUXP_CONFIGDIR"] = global };
        DocumentStore store = new(new CliContext(TextWriter.Null, TextWriter.Null, _root, environment, TestContext.Current.CancellationToken));

        Assert.Equal(Path.Combine(global, "project.yaml"), store.Resolve("project"));
        Assert.Equal(Path.Combine(local, ".tmuxp.yaml"), store.Resolve("./project"));
        Assert.Equal(Path.Combine(_root, "project.yaml"), store.Resolve("project.yaml"));
        Assert.Throws<CliException>(() => store.Resolve("missing.yaml"));
        Assert.Equal(Path.Combine(global, "project.yaml"), store.Resolve("project", global));
    }

    [Fact]
    public async Task Global_discovery_excludes_hidden_files()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".hidden.yaml"), "session_name: hidden", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.yaml"), "session_name: visible", TestContext.Current.CancellationToken);
        var result = await Run("ls", "--json");
        Assert.Single(JsonNode.Parse(result.Output)!["workspaces"]!.AsArray());
    }

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        Dictionary<string, string?> environment = new(StringComparer.Ordinal) { ["HOME"] = _root, ["TMUXP_CONFIGDIR"] = _root, ["PATH"] = "/no-executables", ["NO_COLOR"] = "1" };
        int code = await CliRunner.RunAsync(args, output, error, _root, environment, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
