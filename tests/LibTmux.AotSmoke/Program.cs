using System.Diagnostics.CodeAnalysis;
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
    private sealed class QueryRow
    {
        private readonly string _sessionName;

        internal QueryRow(string sessionName) => _sessionName = sessionName;

        public string SessionName => _sessionName;
    }

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
        TmuxTestOptions options = new(new ServerConnectionOptions
            {
                TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                SocketName = $"libtmux-aot-{Guid.NewGuid():N}"[..24],
                ConfigurationFile = "/dev/null",
            });

        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(options);
        {
            QueryDocument query =
                QueryEdgeParser.ParseNameContains(QueryTarget.Session, "aot");
            bool queryRoundTrips =
                QueryJson.Deserialize(QueryJson.Serialize(query)) == query;
            bool queryMatches = CompileQuery(query);
            bool queryTranslates = TranslateQuery();
            Server server = scope.Server;
            Session session = scope.Session;
            Window window = scope.Window;
            Pane pane = scope.Pane;

            await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"));
            TmuxOption option = (await window.Options.GetAsync(
                new GetOptionRequest("automatic-rename")))[0];

            await server.Buffers.SetAsync("aot", "libtmux-aot");
            string buffer = await server.Buffers.GetAsync("libtmux-aot");

            Server inspected = await server.InspectAsync()
                ?? throw new InvalidOperationException("The owned daemon disappeared.");
            PaneId wanted = pane.Id;
            QueryDocument source = QueryExtensions.Translate<Pane>(candidate => candidate.Id == wanted);
            QueryResult<Pane> exact = await source.Plan<Pane>(inspected.DaemonVersion!.Value, QueryPushdown.Require)
                .ExecuteAsync(inspected);
            string sessionName = session.Name;
            QueryDocument graph = QueryExtensions.Translate<Window>(candidate => candidate.Panes.Any()
                && candidate.LinkedSessions.Any(parent => parent.Name == sessionName));
            QueryPlan<Window> graphPlan = graph.Plan<Window>(inspected.DaemonVersion.Value);
            QueryResult<Window> related = await graphPlan.ExecuteAsync(inspected);
            bool sourceQueries = exact.Count == 1 && exact[0].Id == wanted
                && related.Count == 1 && related[0].Panes.Count == 1
                && related[0].LinkedSessions.Single().Name == sessionName
                && graphPlan.PushedPredicate is null && graphPlan.ResidualPredicate is not null;
            Console.WriteLine($"query-source {sourceQueries}");

            Console.WriteLine($"session {session.Name}");
            Console.WriteLine($"pane    {pane.Width}x{pane.Height}");
            Console.WriteLine($"option  {option.Value.Raw}");
            Console.WriteLine($"buffer  {buffer}");
            Console.WriteLine($"query-json {queryRoundTrips}");
            Console.WriteLine($"query-compile {queryMatches}");
            Console.WriteLine($"query-translate {queryTranslates}");
            return option.Value.Boolean == false
                && buffer == "aot"
                && queryRoundTrips
                && queryMatches
                && queryTranslates
                && sourceQueries
                ? 0
                : 1;
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(QueryRow))]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The dynamic dependency preserves the reflected query row.")]
    private static bool CompileQuery(QueryDocument query) =>
        query.Compile<QueryRow>()(new QueryRow("package-aot"));

    private static bool TranslateQuery()
    {
        string expected = "aot";
        QueryDocument query = QueryExtensions.Translate<QueryRow>(
            row => row.SessionName.Contains(expected));
        return CompileQuery(query);
    }
}
