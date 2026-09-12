using System.Text.Json.Nodes;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace LibTmux.Workspace.Cli.Tests;

public sealed class CommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", "cli-" + Guid.NewGuid().ToString("N"));

    public CommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Empty_discovery_emits_a_json_object_without_tmux()
    {
        (int code, string output, string error) = await Run("--json", "ls");
        Assert.Equal(0, code);
        Assert.Empty(error);
        JsonNode value = JsonNode.Parse(output)!;
        Assert.Empty(value["workspaces"]!.AsArray());
        Assert.NotNull(value["global_workspace_dirs"]);
    }

    [Theory]
    [InlineData("load", "--unknown")]
    [InlineData("import", "teamocil")]
    [InlineData("import", "tmuxinator")]
    [InlineData("load", "file.yaml", "-2", "-8")]
    [InlineData("load", "file.yaml", "-2", "--88-colors")]
    public async Task Usage_failure_precedes_discovery_or_tmux(params string[] args)
    {
        (int code, string output, string error) = await Run(["--json", .. args]);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Equal("usage", JsonNode.Parse(error)!["code"]!.ToString());
        if (args.Contains("-2")) Assert.Contains("cannot be combined", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-8", "")]
    [InlineData("-8", "--json")]
    [InlineData("-8", "--ndjson")]
    [InlineData("--88-colors", "")]
    [InlineData("--88-colors", "--json")]
    [InlineData("--88-colors", "--ndjson")]
    public async Task Unsupported_colors_fail_before_input_discovery(string colors, string mode)
    {
        string[] outputMode = mode.Length == 0 ? [] : [mode];
        var result = await Run(["load", "missing.yaml", colors, .. outputMode]);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        if (mode.Length > 0) Assert.Equal("unsupported-color-mode", JsonNode.Parse(result.Error)!["code"]!.ToString());
        Assert.Contains("88-color", result.Error, StringComparison.Ordinal);
        Assert.Contains("-2", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_search_is_an_array_and_ndjson_wins()
    {
        (int code, string output, _) = await Run("search", "none", "--json");
        Assert.Equal(0, code);
        Assert.Empty(JsonNode.Parse(output)!.AsArray());
        (code, output, _) = await Run("search", "none", "--json", "--ndjson");
        Assert.Equal(0, code);
        Assert.Empty(output);
    }

    [Fact]
    public async Task Machine_conversion_preserves_unknown_keys_without_writing_a_file()
    {
        string file = Path.Combine(_root, "sample.yaml");
        await File.WriteAllTextAsync(file, "session_name: demo\nwindows: []\ncustom: {value: retained}\n", TestContext.Current.CancellationToken);
        (int code, string output, _) = await Run("convert", file, "--json");
        Assert.Equal(0, code);
        Assert.Equal("retained", JsonNode.Parse(output)!["custom"]!["value"]!.GetValue<string>());
        Assert.False(File.Exists(Path.ChangeExtension(file, ".json")));
    }

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        Dictionary<string, string?> environment = new() { ["HOME"] = _root, ["TMUXP_CONFIGDIR"] = Path.Combine(_root, "missing"), ["XDG_CONFIG_HOME"] = Path.Combine(_root, "xdg"), ["PATH"] = "/no-executables", ["NO_COLOR"] = "1" };
        int code = await CliRunner.RunAsync(args, output, error, _root, environment, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
