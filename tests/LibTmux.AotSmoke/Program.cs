using System.Runtime.Versioning;
using LibTmux.Query;
using LibTmux.Query.Json;
using LibTmux.Testing;

namespace LibTmux.AotSmoke;

/// <summary>Drives the library from an ahead-of-time published binary.</summary>
/// <remarks>
/// Trim/AOT warnings only surface once something is published that way and
/// run; this exercises the surface a caller reaches without an expression tree.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("tmux does not run on Windows.");
            return 1;
        }

        // Connecting reads a running server's generation, so the server is
        // started rather than assumed. The scope kills it on the way out.
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"libtmux-aot-{Guid.NewGuid():N}"[..24],
            configurationFile: "/dev/null"));

        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(options);
        {
            QueryDocument query =
                QueryEdgeParser.ParseNameContains(QueryTarget.Session, "aot");
            bool queryRoundTrips =
                QueryJson.Deserialize(QueryJson.Serialize(query)) == query;
            Server server = scope.Server;
            Session session = scope.Session;
            Window window = scope.Window;
            Pane pane = scope.Pane;

            await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"));
            TmuxOption option = (await window.Options.GetAsync(
                new GetOptionRequest("automatic-rename")))[0];

            await server.SetBufferAsync("aot", "libtmux-aot");
            string buffer = await server.GetBufferAsync("libtmux-aot");

            Console.WriteLine($"session {session.Name}");
            Console.WriteLine($"pane    {pane.Width}x{pane.Height}");
            Console.WriteLine($"option  {option.Value.Raw}");
            Console.WriteLine($"buffer  {buffer}");
            Console.WriteLine($"query-json {queryRoundTrips}");
            return option.Value.Boolean == false && buffer == "aot" && queryRoundTrips ? 0 : 1;
        }
    }
}
