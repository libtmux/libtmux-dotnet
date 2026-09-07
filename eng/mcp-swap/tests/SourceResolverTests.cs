namespace LibTmux.McpSwap.Tests;

public sealed class SourceResolverTests
{
    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("release", "Release")]
    public void BuildSourcesPointDirectlyAtNewestFrameworkApphost(
        string sourceName,
        string configuration)
    {
        SourceKind source = Enum.Parse<SourceKind>(sourceName, ignoreCase: true);
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root);
        string expected = Path.Combine(
            repository,
            "src",
            "LibTmux.Mcp",
            "bin",
            configuration,
            "net10.0",
            "LibTmux.Mcp");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, string.Empty);

        SourcePlan plan = SourceResolver.Resolve(
            Options(source, repository),
            "/opt/dotnet/dotnet",
            paths.StateHome);

        Assert.Equal(expected, plan.Spec.Command);
        Assert.Empty(plan.Spec.Arguments);
        Assert.Equal("/opt/dotnet", plan.Spec.Environment["DOTNET_ROOT"]);
        Assert.Equal("tmux", plan.ServerName);
    }

    [Fact]
    public void RunSourceUsesPinnedDotnetAndExplicitProjectFramework()
    {
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root);

        SourcePlan plan = SourceResolver.Resolve(
            Options(SourceKind.Run, repository),
            "/opt/dotnet/dotnet",
            paths.StateHome);

        Assert.Equal("/opt/dotnet/dotnet", plan.Spec.Command);
        Assert.Equal(
            [
                "run",
                "--project",
                Path.Combine(repository, "src", "LibTmux.Mcp", "LibTmux.Mcp.csproj"),
                "--framework",
                "net10.0",
                "--configuration",
                "Debug",
                "--",
            ],
            plan.Spec.Arguments);
    }

    [Fact]
    public void PublishedAndPathSourcesUseImmutableAbsoluteLaunchers()
    {
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root);
        string binary = Path.Combine(paths.Root, "custom", "server");
        CommandOptions pathOptions = Options(SourceKind.Path, repository) with { BinaryPath = binary };
        CommandOptions publishedOptions = Options(SourceKind.Published, repository) with { Version = "0.1.0-alpha.3" };

        SourcePlan path = SourceResolver.Resolve(pathOptions, "/opt/dotnet/dotnet", paths.StateHome);
        SourcePlan published = SourceResolver.Resolve(publishedOptions, "/opt/dotnet/dotnet", paths.StateHome);

        Assert.Equal(Path.GetFullPath(binary), path.Spec.Command);
        Assert.Equal(
            Path.Combine(paths.StateHome, "tmux-mcp-dev", "releases", "LibTmux.Mcp-0.1.0-alpha.3", "libtmux-mcp"),
            published.Spec.Command);
    }

    [Fact]
    public void PublishedSourceRejectsAnUnsafeVersionFromProgrammaticOptions()
    {
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root);
        CommandOptions options = Options(SourceKind.Published, repository) with { Version = "../../escape" };

        Assert.Throws<InvalidDataException>(
            () => SourceResolver.Resolve(options, "/opt/dotnet/dotnet", paths.StateHome));
    }

    [Fact]
    public void ProjectMetadataCannotEscapeTheBuildOrInstallDirectories()
    {
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root, assemblyName: "../escape");

        Assert.Throws<InvalidDataException>(() => SourceResolver.Resolve(
            Options(SourceKind.Debug, repository),
            "/opt/dotnet/dotnet",
            paths.StateHome));
    }

    [Fact]
    public void RepositoryAndServerDefaultsCanBeOverriddenWithoutChangingProjectMetadata()
    {
        using TestPaths paths = new();
        string repository = CreateRepository(paths.Root);

        RepositoryMetadata defaults = SourceResolver.ResolveMetadata(
            Options(SourceKind.Debug, repository));
        RepositoryMetadata overridden = SourceResolver.ResolveMetadata(
            Options(SourceKind.Debug, repository) with { ServerName = "tmux-review" });

        Assert.Equal(Path.GetFullPath(repository), defaults.Repository);
        Assert.Equal("tmux", defaults.ServerName);
        Assert.Equal("LibTmux.Mcp", defaults.BinaryName);
        Assert.Equal("libtmux-mcp", defaults.ToolCommandName);
        Assert.Equal("tmux-review", overridden.ServerName);
    }

    [Fact]
    public void DotnetRootKeepsTheRuntimeDirectoryLauncher()
    {
        using TestPaths paths = new();
        string root = Path.Combine(paths.Root, "dotnet-root");
        Directory.CreateDirectory(root);
        string launcher = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(launcher, string.Empty);

        Assert.Equal(launcher, DotnetLocator.FromRoot(root));
        Assert.Null(DotnetLocator.FromRoot("relative/runtime"));
    }

    private static CommandOptions Options(SourceKind source, string repository) => new()
    {
        Command = SwapCommand.Use,
        Source = source,
        Repository = repository,
    };

    private static string CreateRepository(string root, string assemblyName = "LibTmux.Mcp")
    {
        string repository = Path.Combine(root, "repo");
        string directory = Path.Combine(repository, "src", "LibTmux.Mcp");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "LibTmux.Mcp.csproj"),
            $"<Project><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName><ToolCommandName>libtmux-mcp</ToolCommandName></PropertyGroup></Project>");
        return repository;
    }
}
