namespace LibTmux.McpSwap.Tests;

internal sealed class TestPaths : IDisposable
{
    internal TestPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), $"libtmux-mcp-swap-{Guid.NewGuid():N}");
        Home = Path.Combine(Root, "home");
        ConfigHome = Path.Combine(Root, "config");
        StateHome = Path.Combine(Root, "state");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(ConfigHome);
        Directory.CreateDirectory(StateHome);
    }

    internal string Root { get; }

    internal string Home { get; }

    internal string ConfigHome { get; }

    internal string StateHome { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
