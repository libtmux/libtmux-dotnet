namespace LibTmux.McpSwap.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void UseParsesEverySourceSelectorAndEnvironmentOverlay()
    {
        CommandOptions options = CommandLine.Parse(
        [
            "use",
            "--source",
            "release",
            "--repo",
            "/tmp/repo",
            "--cli",
            "claude",
            "--cli=codex",
            "--scope",
            "user",
            "--env",
            "LIBTMUX_TOOLSETS=inspect,execute",
            "--dry-run",
            "--no-build",
            "--no-preflight",
        ]);

        Assert.Equal(SwapCommand.Use, options.Command);
        Assert.Equal(SourceKind.Release, options.Source);
        Assert.Equal(["claude", "codex"], options.Clients);
        Assert.Equal(ConfigScope.User, options.Scope);
        Assert.Equal("inspect,execute", options.Environment["LIBTMUX_TOOLSETS"]);
        Assert.True(options.DryRun);
        Assert.True(options.NoBuild);
        Assert.True(options.NoPreflight);
    }

    [Theory]
    [InlineData("use", "--source", "path")]
    [InlineData("use", "--source", "published")]
    [InlineData("detect", "--dry-run")]
    [InlineData("status", "--unknown")]
    [InlineData("use", "--env", "INVALID")]
    public void MissingDependenciesAndIrrelevantFlagsAreRejected(params string[] arguments) =>
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(arguments));

    [Fact]
    public void RevertWithoutScopeMeansAllRecordedClaudeScopes()
    {
        CommandOptions options = CommandLine.Parse(["revert", "--cli", "claude"]);

        Assert.Null(options.Scope);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("use")]
    [InlineData("revert")]
    public void ScopeIsAvailableOnEveryScopeAwareCommand(string command)
    {
        CommandOptions options = CommandLine.Parse([command, "--scope", "user"]);

        Assert.Equal(ConfigScope.User, options.Scope);
    }

    [Fact]
    public void DeprecatedSafetyEnvironmentCannotBeWritten()
    {
        CommandLineException failure = Assert.Throws<CommandLineException>(
            () => CommandLine.Parse(["use", "--env", "LIBTMUX_SAFETY=read-only"]));

        Assert.Contains("LIBTMUX_SAFETY has been removed", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("1.0.0/escape")]
    [InlineData("1.0.0 *")]
    public void PublishedVersionMustBeOneSafeExactPathComponent(string version)
    {
        CommandLineException failure = Assert.Throws<CommandLineException>(
            () => CommandLine.Parse(["use", "--source", "published", "--version", version]));

        Assert.Contains("safe exact version", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--project", "../outside")]
    [InlineData("--entry", "..\\outside")]
    public void BuildPathSelectorsMustBeSingleComponents(string option, string value)
    {
        CommandLineException failure = Assert.Throws<CommandLineException>(
            () => CommandLine.Parse(["use", option, value]));

        Assert.Contains("path component", failure.Message, StringComparison.Ordinal);
    }
}
