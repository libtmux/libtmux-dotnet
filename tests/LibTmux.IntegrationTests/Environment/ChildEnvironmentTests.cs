using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

// A namespace segment named Environment would shadow System.Environment
// for every sibling file in this assembly, so these tests stay in the
// assembly root namespace even though the folder groups them.
namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class ChildEnvironmentTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public void Starting_server_removes_inherited_tmux_without_mutating_process_environment()
    {
        const string inherited = "/tmp/inherited-socket,4242,0";
        string? beforeReal = Environment.GetEnvironmentVariable(TmuxEnvironmentVariables.ServerVariable);
        var startInfo = new ProcessStartInfo("/bin/true");
        // Seeded on the child's own block, never on this process: another
        // test collection reads the ambient TMUX identity concurrently, and
        // mutating the real environment here raced it.
        startInfo.Environment[TmuxEnvironmentVariables.ServerVariable] = inherited;

        ChildProcessEnvironment.Apply(
            startInfo,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TMUX_TMPDIR"] = "/tmp/isolated",
                ["LIBTMUX_REMOVED"] = null,
            });

        // The child must not inherit a pane's server, or a bare client
        // would target the wrong socket or refuse to nest.
        Assert.False(startInfo.Environment.ContainsKey(TmuxEnvironmentVariables.ServerVariable));
        Assert.Equal("/tmp/isolated", startInfo.Environment["TMUX_TMPDIR"]);
        Assert.False(startInfo.Environment.ContainsKey("LIBTMUX_REMOVED"));
        // Isolation belongs to the child, never to this process.
        Assert.Equal(
            beforeReal,
            Environment.GetEnvironmentVariable(TmuxEnvironmentVariables.ServerVariable));
    }
}
