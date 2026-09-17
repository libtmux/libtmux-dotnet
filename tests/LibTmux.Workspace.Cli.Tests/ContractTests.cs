using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    public async Task Word_search_bounds_the_whole_alternation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "one.yaml"), "session_name: alpha\nwindows: []\n", token);
        await File.WriteAllTextAsync(Path.Combine(_root, "two.yaml"), "session_name: alphax\nwindows: []\n", token);
        await File.WriteAllTextAsync(Path.Combine(_root, "three.yaml"), "session_name: unbeta\nwindows: []\n", token);
        var result = await Run("search", "s:alpha|beta", "--word-regexp", "--json");
        Assert.Equal(0, result.Code);
        Assert.Equal("alpha", Assert.Single(JsonNode.Parse(result.Output)!.AsArray())!["session_name"]!.ToString());
    }

    [Fact]
    public async Task Search_reports_a_pattern_that_outruns_its_match_budget()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "one.yaml"), "session_name: " + new string('a', 40) + "b\nwindows: []\n", TestContext.Current.CancellationToken);
        var result = await Run("search", "s:(a+)+$", "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Equal("pattern_timeout", JsonNode.Parse(result.Error)!["code"]!.ToString());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_list_shows_windows_layouts_and_pane_commands_without_tmux(bool tree)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dev.yaml"), FullListDocument, TestContext.Current.CancellationToken);
        string[] arguments = ["ls", .. tree ? new[] { "--tree" } : []];
        var brief = await Run(arguments);
        Assert.Equal(0, brief.Code);
        Assert.DoesNotContain("editor", brief.Output, StringComparison.Ordinal);

        var full = await Run([.. arguments, "--full", "--color", "never"]);
        Assert.Equal(0, full.Code);
        Assert.Empty(full.Error);
        Assert.Contains("~/dev.yaml", full.Output, StringComparison.Ordinal);
        Assert.Contains("editor [even-horizontal]", full.Output, StringComparison.Ordinal);
        Assert.Contains("pane 0: echo [red]literal[/]", full.Output, StringComparison.Ordinal);
        Assert.Contains("pane 1: echo structured", full.Output, StringComparison.Ordinal);
        Assert.Contains("pane 2: echo scalar", full.Output, StringComparison.Ordinal);
        Assert.Contains("pane 3: echo nested", full.Output, StringComparison.Ordinal);
        Assert.Contains("pane 4: echo \\u001b[31mcontrol", full.Output, StringComparison.Ordinal);
        string[] lines = full.Output.Split('\n').Select(line => line.Trim()).ToArray();
        int unnamed = Array.IndexOf(lines, "window 1");
        Assert.True(unnamed >= 0);
        Assert.EndsWith("pane 0", lines[unnamed + 1], StringComparison.Ordinal);
        Assert.Contains("empty", lines);
        Assert.DoesNotContain("\u001b", full.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--ndjson")]
    public async Task Full_list_preserves_complete_machine_configuration(string mode)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dev.yaml"), FullListDocument, TestContext.Current.CancellationToken);
        var result = await Run("ls", "--tree", "--full", mode);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        JsonNode output = JsonNode.Parse(result.Output)!;
        JsonNode row = mode == "--json" ? output["workspaces"]![0]! : output;
        Assert.True(JsonNode.DeepEquals(DocumentStore.Parse(FullListDocument), row["config"]));
        Assert.DoesNotContain("\u001b", result.Output, StringComparison.Ordinal);
    }

    private const string FullListDocument = """
        session_name: project
        windows:
          - window_name: editor
            layout: even-horizontal
            panes:
              - 'echo [red]literal[/]'
              - shell_command: [{cmd: echo structured, enter: false}]
              - shell_command: echo scalar
              - [echo nested, echo next]
              - "echo \e[31mcontrol"
          - panes: [null]
          - window_name: empty
            panes: []
        """;

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

    // fish offered every subcommand's flags on every subcommand -- `load
    // --<Tab>` showed 38 instead of load's own 11. Each `complete` line must
    // name the exact subcommand path it belongs to.
    [Fact]
    public async Task Fish_completion_scopes_flags_to_their_own_subcommand()
    {
        var result = await Run("--generate", "fish");
        Assert.Equal(0, result.Code);
        string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("complete -c tmux-workspace -f -n ", line, StringComparison.Ordinal));
        string[] loadLines = [.. lines.Where(line => line.Contains("__fish_seen_subcommand_from load;", StringComparison.Ordinal))];
        Assert.NotEmpty(loadLines);
        // --bpython/--pdb belong to shell, --fixed-strings/--any to search,
        // --force/--save-to to freeze/convert/import: none are load's.
        Assert.All(loadLines, line => Assert.DoesNotContain("bpython", line, StringComparison.Ordinal));
        Assert.All(loadLines, line => Assert.DoesNotContain("fixed-strings", line, StringComparison.Ordinal));
        Assert.All(loadLines, line => Assert.DoesNotContain("-l force", line, StringComparison.Ordinal));
        Assert.Contains(loadLines, line => line.Contains(" -l append ", StringComparison.Ordinal));
        Assert.Contains(loadLines, line => line.Contains(" -s d ", StringComparison.Ordinal));
        // json/ndjson/color are recursive: always available, not scoped to
        // any one subcommand's own condition.
        Assert.Contains(lines, line => line.Contains("-n 'true'", StringComparison.Ordinal) && line.Contains(" -l json ", StringComparison.Ordinal));
    }

    // Same bug as fish's, one shell over: bash and zsh offered one flat
    // vocabulary shared by every subcommand, so `ls --<Tab>` offered
    // load-only flags like --append. Each generated case branch must list
    // only its own subcommand's flags.
    private static string OptsForCase(string script, string label)
    {
        Match match = Regex.Match(script, @"(?m)^\s*" + Regex.Escape(label) + @"\)\s*opts='([^']*)'");
        Assert.True(match.Success, $"No '{label}' case branch found in:\n{script}");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Bash_completion_scopes_flags_to_their_own_subcommand()
    {
        var result = await Run("--generate", "bash");
        Assert.Equal(0, result.Code);
        string[] load = OptsForCase(result.Output, "load").Split(' ');
        string[] ls = OptsForCase(result.Output, "ls").Split(' ');
        Assert.Contains("--append", load);
        Assert.DoesNotContain("--append", ls);
        Assert.Contains("--tree", ls);
        Assert.DoesNotContain("--tree", load);
        // json/ndjson/color are recursive: on both, despite neither owning them.
        Assert.Contains("--json", load);
        Assert.Contains("--json", ls);
    }

    [Fact]
    public async Task Zsh_completion_scopes_flags_to_their_own_subcommand()
    {
        var result = await Run("--generate", "zsh");
        Assert.Equal(0, result.Code);
        Assert.StartsWith("#compdef tmux-workspace\n", result.Output, StringComparison.Ordinal);
        string[] load = OptsForCase(result.Output, "load").Split(' ');
        string[] ls = OptsForCase(result.Output, "ls").Split(' ');
        Assert.Contains("--append", load);
        Assert.DoesNotContain("--append", ls);
        Assert.Contains("--tree", ls);
        Assert.DoesNotContain("--tree", load);
        Assert.Contains("--json", load);
        Assert.Contains("--json", ls);
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
    public async Task Help_shortcut_stops_at_the_argument_separator()
    {
        var result = await Run("load", "--json", "-d", "--", "-h");
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Equal("workspace_not_found", JsonNode.Parse(result.Error)!["code"]!.ToString());
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
