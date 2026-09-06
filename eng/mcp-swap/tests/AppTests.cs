namespace LibTmux.McpSwap.Tests;

public sealed class AppTests
{
    [Fact]
    public void HelpPrintsNativeCommandSurface()
    {
        StringWriter output = new();
        StringWriter error = new();

        int result = McpSwapApp.Execute(["--help"], output, error);

        Assert.Equal(0, result);
        Assert.Contains("detect|status|use|revert|doctor", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void UnknownOptionFailsWithoutRunningACommand()
    {
        StringWriter output = new();
        StringWriter error = new();

        int result = McpSwapApp.Execute(["detect", "--dry-run"], output, error);

        Assert.Equal(2, result);
        Assert.Contains("does not apply", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnscopedClaudeStatusShowsUserAndProjectLayers()
    {
        using TestPaths paths = new();
        string repository = Path.Combine(paths.Root, "repo");
        string project = Path.Combine(repository, "src", "LibTmux.Mcp");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "LibTmux.Mcp.csproj"),
            "<Project><PropertyGroup><AssemblyName>LibTmux.Mcp</AssemblyName></PropertyGroup></Project>");
        ClientCatalog catalog = ClientCatalog.Create(paths.Home, paths.ConfigHome);
        ClientInfo claude = catalog["claude"];
        byte[] config = ConfigCodec.Set(
            claude,
            "{}"u8.ToArray(),
            "tmux",
            new ServerSpec("user-server"),
            repository,
            ConfigScope.User).Bytes;
        config = ConfigCodec.Set(
            claude,
            config,
            "tmux",
            new ServerSpec("project-server"),
            repository,
            ConfigScope.Project).Bytes;
        File.WriteAllBytes(claude.ConfigPath, config);
        StringWriter output = new();

        McpSwapApp.Status(
            catalog,
            new SwapRuntime(paths.Home, paths.ConfigHome, paths.StateHome, string.Empty),
            new CommandOptions
            {
                Command = SwapCommand.Status,
                Repository = repository,
                Clients = ["claude"],
            },
            output,
            TextWriter.Null);

        Assert.Contains("[claude:user] tmux = user-server", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("[claude:project] tmux = project-server", output.ToString(), StringComparison.Ordinal);
    }
}
