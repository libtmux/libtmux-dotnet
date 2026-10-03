using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Query;
using LibTmux.Query.Json;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Query;

/// <summary>Filters the objects the library hands back, declaratively.</summary>
/// <remarks>
/// Translating and interpreting have to resolve the same pair: tmux calls a
/// field <c>session_name</c> and C# calls it <c>Name</c>. When only one side
/// knew that, a filter over a real session threw rather than matching, and the
/// only expressions that worked were over rows whose properties happened to be
/// spelled the way the wire is.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class EntityFilterTests
{
    [UnixFact]
    public async Task Pane_dimensions_match_native_filters_after_daemon_exit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["resize-window", "-t", "$0:0", "-x", "100", "-y", "30"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-h", "-l", "40", "-t", "$0:0", "exec /bin/cat"], token)).ExitCode);
        Server server = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        });
        Server inspected = Assert.IsType<Server>(await server.InspectAsync(token));
        QueryDocument dimensions = QueryExtensions.Translate<Pane>(pane => pane.Width >= 50 && pane.Height == 30);
        QueryDocument restored = QueryJson.Deserialize(QueryJson.Serialize(dimensions));
        foreach (QueryPushdown mode in new[] { QueryPushdown.Never, QueryPushdown.Auto })
        {
            QueryResult<Pane> result = await restored.Plan<Pane>(inspected.DaemonVersion!.Value, mode).ExecuteAsync(server, token);
            Assert.Equal(2, result.Snapshot.Panes.Count);
            Assert.Equal(result.Snapshot.Panes.Where(pane => pane.Width >= 50 && pane.Height == 30), result);
            Assert.Equal(59, Assert.Single(result).Width);
        }
        Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        Assert.Equal(captured.Panes.Where(pane => pane.Width >= 50 && pane.Height == 30),
            captured.Panes.Matching(restored));
        Assert.Equal(59, Assert.Single(captured.Panes.Matching(restored)).Width);
    }

    [UnixFact]
    public async Task Pane_text_queries_bind_captured_entities_without_io()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null"
            }, token);
        Pane pane = Assert.Single(await server.GetPanesAsync(token));
        string command = Assert.IsType<string>(pane.RawFormatFields["pane_current_command"]);
        string path = Assert.IsType<string>(pane.RawFormatFields["pane_current_path"]);
        Assert.NotEmpty(command);
        Assert.NotEmpty(path);
        QueryDocument document = new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            QueryTarget.Pane,
            new ComparisonNode(
                QueryComparison.Equal,
                new FieldNode(QueryTarget.Pane, "pane_command"),
                new ConstantNode(new StringConstant(command))));
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);

        Assert.Multiple(
            () => Assert.Equal(command, pane.CurrentCommand),
            () => Assert.Equal(path, pane.CurrentPath),
            () => Assert.Equal(pane, Assert.Single(new[] { pane }.Matching(document))));
        Assert.Equal([pane], new[] { pane }.Matching<Pane>(candidate => candidate.CurrentCommand == command));
        Assert.Equal([pane], new[] { pane }.Where(candidate => candidate.CurrentPath == path));
        QueryDocument paths = QueryExtensions.Translate<Pane>(
            candidate => candidate.CurrentPath == path);
        Assert.Equal(2, paths.Version);
        Assert.Equal([pane], new[] { pane }.Matching(
            QueryJson.Deserialize(QueryJson.Serialize(paths))));
        Assert.Empty(new[] { pane }.Matching(QueryExtensions.Translate<Pane>(
            candidate => candidate.CurrentPath == "/not-the-captured-path")));
    }

    [UnixFact]
    public async Task A_predicate_over_sessions_matches_what_tmux_reports()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);

        await scope.Server.CreateSessionAsync(new NewSessionRequest { Name = "build-one" }, token);
        await scope.Server.CreateSessionAsync(new NewSessionRequest { Name = "build-two" }, token);
        await scope.Server.CreateSessionAsync(new NewSessionRequest { Name = "other" }, token);

        IReadOnlyList<Session> sessions = await scope.Server.GetSessionsAsync(token);
        IReadOnlyList<Session> building = sessions.Matching<Session>(
            session => session.Name.StartsWith("build", StringComparison.Ordinal));

        Assert.Equal(2, building.Count);
        Assert.All(building, session => Assert.StartsWith("build", session.Name, StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task The_document_a_predicate_became_filters_the_same_way()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);

        await scope.Session.CreateWindowAsync(new NewWindowRequest { Name = "build-one" }, token);
        await scope.Session.CreateWindowAsync(new NewWindowRequest { Name = "other" }, token);

        // The point of a document is that it can be written somewhere else and
        // still mean this, so the two paths have to agree.
        QueryDocument document = QueryExtensions.Translate<Window>(
            window => window.Name.StartsWith("build", StringComparison.Ordinal));
        IReadOnlyList<Window> windows = await scope.Session.GetWindowsAsync(token);

        Assert.Equal(
            windows.Matching<Window>(
                window => window.Name.StartsWith("build", StringComparison.Ordinal)).Count,
            windows.Matching(document).Count);
        Assert.Single(windows.Matching(document));
    }

    [UnixFact]
    public async Task A_relation_quantifier_reads_the_captured_windows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);
        await scope.Session.CreateWindowAsync(new NewWindowRequest { Name = "build-one" }, token);

        Server snapshot = await scope.Server.CaptureSnapshotAsync(SnapshotDepth.Windows, token);
        IReadOnlyList<Session> sessions = [.. snapshot.Sessions];

        IReadOnlyList<Session> building = sessions.Matching<Session>(
            session => session.Windows.Any(window => window.Name.StartsWith("build", StringComparison.Ordinal)));

        Assert.Single(building);
    }

    private static Task<TemporaryHierarchyScope> Scope(CancellationToken cancellationToken)
    {
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltquery-{Guid.NewGuid():N}"[..24],
            ConfigurationFile = "/dev/null",
        });
        return factory.CreateHierarchyAsync(options, cancellationToken);
    }

    [UnixFact]
    public async Task Graph_queries_preserve_linked_placements_without_io()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null"
            }, token);
        Assert.Equal(0, (await raw.ExecuteAsync(["rename-window", "-t", "$0:0", "editor"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$0:5"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", "other"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$1:3"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["select-window", "-t", "$0:5"], token)).ExitCode);
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Window uncaptured = (await server.GetWindowsAsync(token))[0];
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);

        QueryDocument linked = QueryExtensions.Translate<Window>(
            window => window.Name == "editor"
                && window.LinkedSessions.Any(session => session.Name == "other"));
        IReadOnlyList<Window> placements = snapshot.Windows.Matching(
            QueryJson.Deserialize(QueryJson.Serialize(linked)));
        Assert.Equal(3, placements.Count);
        Assert.Equal(3, placements.Select(window => window.EntityKey).Distinct().Count());
        Assert.Single(placements.Select(window => window.Id).Distinct());
        Assert.Throws<IncompleteSnapshotException>(() => new[] { uncaptured }.Matching(linked));

        QueryDocument active = QueryExtensions.Translate<Session>(
            session => session.ActiveWindow.Value.Name == "editor");
        Assert.Equal(raw.SessionName, Assert.Single(snapshot.Sessions.Matching(active)).Name);
        QueryDocument selectedPlacement = QueryExtensions.Translate<Window>(
            window => window.IsActive && window.Index == 5);
        Assert.Equal(5, Assert.Single(snapshot.Windows.Matching(selectedPlacement)).Index);
        Assert.Equal([false, true, false], placements.Select(window => window.IsActive));
        Assert.Single(snapshot.Sessions.Matching(QueryExtensions.Translate<Session>(
            session => session.Windows.Any(window => window.IsActive && window.Name == "editor"))));
        Assert.Equal(2, snapshot.Sessions.Matching(QueryExtensions.Translate<Session>(
            session => session.Windows.Any(window => window.IsActive)
                && session.Windows.Any(window => window.Name == "editor"))).Count);
        Assert.Equal(3, snapshot.Windows.Matching(QueryExtensions.Translate<Window>(
            window => window.LinkedSessions.Count == 2 && window.LinkedSessions.Any())).Count);
        QueryDocument parent = QueryExtensions.Translate<Pane>(
            pane => pane.Window.Session.Name == "other" && pane.Window.Name == "editor");
        Assert.Equal(3, Assert.Single(snapshot.Panes.Matching(parent)).Window.Index);
    }
    [UnixFact]
    public async Task Source_execution_preserves_same_observation_placements_and_graph()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        List<IReadOnlyList<string>> commands = [];
        Server endpoint = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
            InitializeAsync = (_, _) => throw new InvalidOperationException("Source query initialized the endpoint."),
            Interceptor = (invocation, next, cancellation) =>
            {
                commands.Add(invocation.Arguments);
                return next(cancellation);
            },
        });
        Assert.Equal(0, (await raw.ExecuteAsync(["rename-window", "-t", "$0:0", "editor"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$0:5"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", "other"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$1:3"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["select-window", "-t", "$0:5"], token)).ExitCode);
        Server inspected = Assert.IsType<Server>(await endpoint.InspectAsync(token));
        TmuxVersion version = inspected.DaemonVersion!.Value;
        WindowId wanted = new(0);
        QueryDocument ids = QueryExtensions.Translate<Window>(window => window.Id == wanted);
        foreach (QueryPushdown mode in Enum.GetValues<QueryPushdown>())
        {
            QueryPlan<Window> plan = ids.Plan<Window>(version, mode);
            commands.Clear();
            QueryResult<Window> selected = await plan.ExecuteAsync(endpoint, token);
            Assert.Equal(3, selected.Count);
            Assert.Equal(4, selected.Snapshot.Windows.Count);
            Assert.Equal(selected.Snapshot.Windows.Matching(ids).Select(window => window.EntityKey),
                selected.Select(window => window.EntityKey));
            Assert.Equal(3, selected.Select(window => window.EntityKey).Distinct().Count());
            Assert.All(selected, window => Assert.Equal(2, window.LinkedSessions.Count));
            Assert.Single(commands, command => command.Contains("list-windows", StringComparer.Ordinal));
            bool markerSent = commands.Any(command => command[^1].EndsWith(
                $"#{{?{plan.PredicateFormat},1,0}}" + Internal.FormatProjection.RowSeparator, StringComparison.Ordinal));
            Assert.Equal(mode != QueryPushdown.Never, markerSent);
        }
        QueryDocument active = QueryExtensions.Translate<Window>(window => window.IsActive && window.Name == "editor");
        QueryResult<Window> selectedActive = await active.Plan<Window>(version).ExecuteAsync(inspected, token);
        Assert.Equal(5, Assert.Single(selectedActive).Index);
        Assert.Equal(selectedActive.Snapshot.Windows.Matching(active), selectedActive);
        QueryDocument graph = QueryExtensions.Translate<Window>(window => window.Id == wanted && window.Panes.Any()
            && window.LinkedSessions.Any(session => session.Name == "other"));
        QueryResult<Window> retained = await graph.Plan<Window>(version).ExecuteAsync(inspected, token);
        Assert.Equal(3, retained.Count);
        WindowId missing = new(9999);
        QueryDocument none = QueryExtensions.Translate<Window>(window => window.Id == missing);
        QueryResult<Window> empty = await none.Plan<Window>(version, QueryPushdown.Require).ExecuteAsync(inspected, token);
        Assert.Empty(empty);
        Assert.Equal(4, empty.Snapshot.Windows.Count);
        QueryDocument detached = QueryExtensions.Translate<Session>(session => !session.Attached);
        QueryResult<Session> sessions = await detached.Plan<Session>(version, QueryPushdown.Require).ExecuteAsync(inspected, token);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(sessions.Snapshot.Sessions.Matching(detached), sessions);
        PaneId paneId = new(0);
        QueryDocument panes = QueryExtensions.Translate<Pane>(pane => pane.Id == paneId);
        QueryResult<Pane> selectedPanes = await panes.Plan<Pane>(version, QueryPushdown.Require).ExecuteAsync(inspected, token);
        Assert.Equal(3, selectedPanes.Count);
        Assert.Equal(selectedPanes.Snapshot.Panes.Matching(panes), selectedPanes);
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        Assert.Equal(retained.Snapshot.Windows.Matching(graph), retained);
        Assert.All(retained, window => Assert.Same(window, Assert.Single(window.Panes).Window));
        Assert.All(retained, window => Assert.DoesNotContain(window.RawFormatFields.Keys, name => name.Contains("marker", StringComparison.Ordinal)));
    }

    [UnixFact]
    public async Task Source_execution_rejects_cancellation_corrupt_markers_and_replaced_daemons()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool corrupt = true;
        Server endpoint = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
            Interceptor = async (invocation, next, cancellation) =>
            {
                TmuxCommandResult result = await next(cancellation);
                if (!invocation.Arguments.Contains("list-windows", StringComparer.Ordinal))
                {
                    return result;
                }
                if (!corrupt)
                {
                    await cancel.CancelAsync();
                    return result;
                }
                byte[] output = result.StandardOutput.ToArray();
                int marker = output.Length - Internal.FormatProjection.RowSeparator.Length - 2;
                Assert.Equal((byte)'0', output[marker]);
                output[marker] = (byte)'2';
                return new TmuxCommandResult(result.Arguments, result.ExitCode, output, result.StandardError,
                    Internal.Utf8BackslashDecoder.ProjectOutputLines(output), result.StandardErrorLines);
            },
        });
        Server inspected = Assert.IsType<Server>(await endpoint.InspectAsync(token));
        WindowId missing = new(9999);
        QueryPlan<Window> plan = QueryExtensions.Translate<Window>(window => window.Id == missing)
            .Plan<Window>(inspected.DaemonVersion!.Value, QueryPushdown.Require);
        await Assert.ThrowsAsync<TmuxProtocolException>(() => plan.ExecuteAsync(inspected, token));
        corrupt = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.ExecuteAsync(inspected, cancel.Token));
        using Process daemon = Process.GetProcessById(Assert.IsType<ServerGeneration>(inspected.Generation).ProcessId);
        Task exited = daemon.WaitForExitAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        await exited;
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", "replacement"], token)).ExitCode);
        await Assert.ThrowsAsync<StaleServerGenerationException>(() => plan.ExecuteAsync(inspected, token));
    }

}
