using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
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
    [UnixFact]
    public async Task C_locale_clients_preserve_numeric_tabs_and_unicode()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?>
            {
                ["LANG"] = "C",
                ["LC_ALL"] = "C",
                ["LC_CTYPE"] = "C",
            },
        }, token);
        Pane pane = Assert.Single(await server.GetPanesAsync(token));
        const string Format = "#{pane_pid}\t#{history_size}\t#{history_limit}\t#{pane_height}"
            + "\t#{cursor_y}\t#{pane_dead}\t#{alternate_on}\t観測-é";
        IReadOnlyList<string>? process = await pane.DisplayMessageAsync(
            new DisplayMessageRequest { Message = Format, ReturnText = true }, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);
        IReadOnlyList<string> streamed = await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "-t", pane.Id.ToString(), Format), token);

        Assert.All(new[] { Assert.Single(process!), Assert.Single(streamed) }, line =>
        {
            string[] fields = line.Split('\t');
            Assert.Equal(8, fields.Length);
            Assert.All(fields.Take(7), field => Assert.True(int.TryParse(field,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)));
            Assert.Equal("観測-é", fields[7]);
        });
        Assert.Equal(server.Generation, Assert.IsType<Server>(await server.InspectAsync(token)).Generation);
    }

}
