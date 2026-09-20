using System.Runtime.Versioning;
using System.Text;
using LibTmux.Query;
using LibTmux.Query.Json;
using LibTmux.Testing;
using LibTmux.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace LibTmux.PackageConsumer;

/// <summary>Uses the packed library the way a downstream project would.</summary>
/// <remarks>
/// Reaches the library through the built package, not a project reference, to
/// catch a missing assembly, wrong target framework, or gap invisible from
/// inside the repository.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        QueryDocument query =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "package");
        bool queryRoundTrips = QueryJson.Deserialize(QueryJson.Serialize(query)) == query;
        Console.WriteLine($"query-json {queryRoundTrips}");
        if (!queryRoundTrips)
        {
            return 1;
        }

        if (query.Version != QueryDocument.CurrentVersion || query.Version != 2)
        {
            throw new InvalidOperationException("The packed query writer did not use the sole supported schema.");
        }
        try
        {
            _ = QueryJson.Deserialize(QueryJson.Serialize(query).Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal));
            throw new InvalidOperationException("The packed query reader accepted schema v1.");
        }
        catch (UnsupportedQueryExpressionException)
        {
        }

        WorkspaceFile workspace = WorkspaceFile.Parse(
            """
            session_name: package
            windows:
              - window_name: main
                panes:
                  - shell_command: echo package
            """);
        bool workspaceParses = workspace.SessionName == "package"
            && workspace.Windows is [{ Panes: [{ ShellCommands: ["echo package"] }] }];
        Console.WriteLine($"workspace-parse {workspaceParses}");
        if (!workspaceParses)
        {
            return 1;
        }

        WorkspaceFile inherited = WorkspaceFile.Parse(
            """
            session_name: inherited
            start_directory: ${PROJECT}
            environment: { PACKAGE_MODE: root }
            shell_command_before: [echo before]
            windows:
              - start_directory: src
                panes:
                  - start_directory: ../literal-$$-#{window_id}
                    environment: { PACKAGE_MODE: pane }
            """).Resolve(Path.GetTempPath(), new Dictionary<string, string> { ["PROJECT"] = "project" });
        WorkspacePane inheritedPane = inherited.Windows[0].Panes[0];
        if (inheritedPane.StartDirectory != Path.Combine(Path.GetTempPath(), "project", "literal-$-#{window_id}")
            || inherited.Environment["PACKAGE_MODE"] != "root"
            || inheritedPane.Environment["PACKAGE_MODE"] != "pane"
            || inherited.ShellCommandsBefore is not ["echo before"])
        {
            throw new InvalidOperationException("The packed workspace lost explicit path resolution or declaration defaults.");
        }
        Console.WriteLine("workspace-resolution True");

        using (ServiceProvider provider = new ServiceCollection()
            .AddLibTmux(options => options with { SocketName = "package-consumer" })
            .BuildServiceProvider())
        {
            bool injected = provider.GetRequiredService<Server>() is { IsMaterialized: false };
            Console.WriteLine($"dependency-injection {injected}");
            if (!injected)
            {
                return 1;
            }
        }

        if (args is ["--psmux"])
        {
            Console.OutputEncoding = new UTF8Encoding(false, true);
            return await RunPsmuxAsync();
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("usage: LibTmux.PackageConsumer [--psmux]");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("tmux does not run on Windows.");
            return 1;
        }

        return await RunTmuxAsync();
    }

    private static async Task<int> RunPsmuxAsync()
    {
        string executable = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_BINARY")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_BINARY is required.");
        string dataDirectory = Environment.GetEnvironmentVariable("PSMUX_DATA_DIR")
            ?? throw new InvalidOperationException("PSMUX_DATA_DIR is required.");
        string namespaceName = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_NAMESPACE")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_NAMESPACE is required.");
        string expectedText = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_EXPECTED_TEXT")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_EXPECTED_TEXT is required.");

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        PsmuxServer server = await PsmuxServer.ConnectAsync(
            new PsmuxConnectionOptions(
                executable,
                PsmuxServer.SupportedBinarySha256,
                dataDirectory,
                namespaceName),
            budget.Token);
        PsmuxSession session = await server.GetSessionAsync(budget.Token);
        PsmuxWindow window = AssertSingle(await session.GetWindowsAsync(budget.Token), "window");
        PsmuxPane pane = AssertSingle(await window.GetPanesAsync(budget.Token), "pane");
        IReadOnlyList<string> lines = await pane.CaptureAsync(
            new PsmuxCaptureOptions(joinWrappedLines: true),
            budget.Token);
        if (!lines.Any(line => line.Contains(expectedText, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The packed psmux query did not capture the fixture text.");
        }

        Console.WriteLine($"package psmux {session.Id} {window.Id} {pane.Id} {expectedText}");
        return 0;
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<int> RunTmuxAsync()
    {
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"libtmux-pkg-{Guid.NewGuid():N}"[..24],
            ConfigurationFile = "/dev/null",
        });

        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(options);

        Server server = scope.Session.Server;
        await server.ThrowIfDeadAsync();
        Server inspected = await server.InspectAsync()
            ?? throw new InvalidOperationException("Inspection lost the owned daemon.");
        if (inspected.Generation != server.Generation || inspected.DaemonVersion is null || inspected.Sessions.IsCaptured)
        {
            throw new InvalidOperationException("Packed inspection did not preserve its identity-only contract.");
        }
        Window read = await server.GetWindowAsync(scope.Window.Id);
        Pane active = read.ActivePane.Value;
        if (read.Name != scope.Window.Name
            || read.Session.Name != scope.Session.Name
            || read.Width <= 0
            || active.Width <= 0
            || active.Window.Name != read.Name
            || await server.FindPaneAsync(new PaneId(int.MaxValue)) is not null
            || await scope.Session.FindWindowAsync("not-created") is not null)
        {
            throw new InvalidOperationException("The packed lookup lost captured state or absence.");
        }

        await scope.Pane.SendTextAsync("echo consumed-from-the-package");
        string text = await TmuxWait.UntilAsync(
            async token => string.Join(
                '\n',
                await scope.Pane.CaptureAsync(cancellationToken: token)),
            captured => captured.Contains("consumed-from-the-package", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20));

        Console.WriteLine($"session  {scope.Session.Name}");
        Console.WriteLine($"captured {text.Contains("consumed-from-the-package", StringComparison.Ordinal)}");

        WindowCreationResult graphCreation = await scope.Session.CreateWindowWithReceiptAsync(new NewWindowRequest
        {
            Name = "package-graph",
            StartDirectory = Path.GetTempPath(),
            Command = "exec /bin/cat",
        });
        Window graph = graphCreation.Window;
        SessionCreationResult otherCreation = await server.CreateSessionWithReceiptAsync(new NewSessionRequest
        {
            Name = "package-other",
            Command = "exec /bin/cat",
            ExpectedGeneration = inspected.Generation,
        });
        Session other = otherCreation.Session;
        Window otherInitial = AssertSingle(await other.GetWindowsAsync(), "initial window");
        if (otherInitial.Id != otherCreation.InitialWindowId || otherInitial.Index != otherCreation.InitialWindowIndex
            || AssertSingle(await otherInitial.GetPanesAsync(), "initial pane").Id != otherCreation.InitialPaneId
            || AssertSingle(await graph.GetPanesAsync(), "graph pane").Id != graphCreation.InitialPaneId)
        {
            throw new InvalidOperationException("The packed creation receipts lost their acknowledged child identities.");
        }
        Console.WriteLine("creation-receipts True");

        WindowCreationResult scratch = await other.CreateWindowWithReceiptAsync(new()
        {
            Name = "package-guarded",
            Command = "exec /bin/cat",
        });
        Pane initial = await server.GetPaneAsync(scratch.InitialPaneId);
        Pane split = await initial.SplitAsync(new()
        {
            ExpectedWindowId = scratch.Window.Id,
            Command = "exec /bin/cat",
        });
        await RequireNativeRejectionAsync(() => initial.SplitAsync(new() { ExpectedWindowId = graph.Id }));
        await RequireNativeRejectionAsync(() => scratch.Window.UnlinkAsync(true, [initial.Id]));
        await scratch.Window.UnlinkAsync(true, [split.Id, initial.Id]);
        if (await server.FindWindowAsync(scratch.Window.Id) is not null
            || AssertSingle(await other.GetWindowsAsync(), "surviving window").Id != otherInitial.Id)
        {
            throw new InvalidOperationException("The packed guarded cleanup did not preserve the existing window.");
        }
        Console.WriteLine("guarded-mutations True");
        WorkspaceResult built = await ExerciseWorkspaceAsync(server);

        await graph.LinkAsync(new LinkWindowRequest(other.Id.ToString())
        {
            TargetIndex = "5",
            Detach = true,
        });
        Window placement = (await other.GetWindowsAsync()).Single(window => window.Index == 5);
        await placement.SelectAsync();
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes);
        WindowId graphId = graph.Id;
        QueryDocument sourcePredicate = QueryExtensions.Translate<Window>(window => window.Id == graphId);
        QueryResult<Window> sourceMatches = await sourcePredicate.Plan<Window>(inspected.DaemonVersion.Value, QueryPushdown.Require)
            .ExecuteAsync(inspected);
        QueryDocument sourceGraph = QueryExtensions.Translate<Window>(window => window.Id == graphId
            && window.LinkedSessions.Any(session => session.Name == "package-other") && window.Panes.Any());
        QueryPlan<Window> graphPlan = sourceGraph.Plan<Window>(inspected.DaemonVersion.Value);
        QueryResult<Window> sourceGraphMatches = await graphPlan.ExecuteAsync(inspected);
        if (sourceMatches.Count != 2 || sourceMatches.Snapshot.Windows.Count <= sourceMatches.Count
            || !sourceMatches.Select(window => window.EntityKey).SequenceEqual(
                sourceMatches.Snapshot.Windows.Matching(sourcePredicate).Select(window => window.EntityKey))
            || graphPlan.PushedPredicate is not null || graphPlan.ResidualPredicate is null
            || sourceGraphMatches.Count != 2
            || !sourceGraphMatches.SequenceEqual(sourceGraphMatches.Snapshot.Windows.Matching(sourceGraph)))
        {
            throw new InvalidOperationException("The packed source query lost its predicate or snapshot provenance.");
        }
        await scope.DisposeAsync();
        if (!sourceGraphMatches.SequenceEqual(sourceGraphMatches.Snapshot.Windows.Matching(sourceGraph)))
        {
            throw new InvalidOperationException("The packed source result required I/O after daemon termination.");
        }
        Console.WriteLine("query-source True");
        Window builtWindow = AssertSingle(built.Windows, "captured workspace window");
        if (!ReferenceEquals(built.Session, builtWindow.Session) || built.Session.Windows.Count != 1
            || builtWindow.Panes.Count != 2 || builtWindow.ActivePane.Value.Id != builtWindow.Panes[1].Id)
        {
            throw new InvalidOperationException("The packed workspace result lost its offline captured graph.");
        }
        Console.WriteLine("workspace-captured-offline True");

        string path = snapshot.Panes.First(pane => pane.Window.Id == graph.Id).CurrentPath
            ?? throw new InvalidOperationException("The graph fixture did not capture its working directory.");
        QueryDocument pathQuery = QueryExtensions.Translate<Pane>(pane => pane.CurrentPath == path);
        IReadOnlyList<Pane> pathMatches = snapshot.Panes.Matching(QueryJson.Deserialize(QueryJson.Serialize(pathQuery)));
        if (!pathMatches.SequenceEqual(snapshot.Panes.Where(pane => pane.CurrentPath == path))
            || !pathMatches.Any(pane => pane.Window.Id == graph.Id))
        {
            throw new InvalidOperationException("The packed CurrentPath filter disagreed with captured native data.");
        }
        Pane sized = snapshot.Panes.First(pane => pane.Window.Id == graph.Id);
        int width = sized.Width;
        int height = sized.Height;
        QueryDocument dimensions = QueryExtensions.Translate<Pane>(pane => pane.Width >= width && pane.Height == height);
        IReadOnlyList<Pane> dimensionMatches = snapshot.Panes.Matching(QueryJson.Deserialize(QueryJson.Serialize(dimensions)));
        if (!dimensionMatches.SequenceEqual(snapshot.Panes.Where(pane => pane.Width >= width && pane.Height == height))
            || !dimensionMatches.Contains(sized))
        {
            throw new InvalidOperationException("The packed Width/Height filter disagreed with captured native dimensions.");
        }
        Console.WriteLine("query-dimensions-offline True");
        QueryDocument graphQuery = QueryExtensions.Translate<Window>(window => window.Name == "package-graph"
            && window.LinkedSessions.Any(session => session.Name == "package-other"));
        IReadOnlyList<Window> placements = snapshot.Windows.Matching(QueryJson.Deserialize(QueryJson.Serialize(graphQuery)));
        QueryDocument selected = QueryExtensions.Translate<Window>(window => window.IsActive && window.Index == 5);
        QueryDocument parent = QueryExtensions.Translate<Pane>(pane => pane.Window.Session.Name == "package-other"
            && pane.Window.Name == "package-graph");
        QueryDocument descendants = QueryExtensions.Translate<Session>(session => session.Panes.Any(pane => pane.CurrentPath == path));
        if (placements.Count != 2 || placements.Select(window => window.EntityKey).Distinct().Count() != 2
            || placements.Select(window => window.Id).Distinct().Single() != graph.Id
            || snapshot.Windows.Matching(selected).Single().Session.Id != other.Id
            || snapshot.Panes.Matching(parent).Single().Window.Index != 5
            || !snapshot.Sessions.Matching(descendants).Select(session => session.Id)
                .SequenceEqual(snapshot.Sessions.Where(session => session.Panes.Any(pane => pane.CurrentPath == path)).Select(session => session.Id)))
        {
            throw new InvalidOperationException("The packed graph filter lost placement or relationship context.");
        }
        Console.WriteLine("query-graph-offline True");
        return 0;
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<WorkspaceResult> ExerciseWorkspaceAsync(Server server)
    {
        WorkspaceFile declaration = new("package-workspace",
            options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat", ["base-index"] = "3" },
            windows: [new WorkspaceWindow("editor", focus: true, panes: [new WorkspacePane(), new WorkspacePane(focus: true)])]);
        WorkspaceBuilder.Validate(declaration);
        try
        {
            WorkspaceBuilder.Validate(new WorkspaceFile("incomplete"));
            throw new InvalidOperationException("The packed workspace preflight accepted an incomplete declaration.");
        }
        catch (WorkspaceFormatException)
        {
        }
        WorkspaceBuilder builder = new(server);
        WorkspacePlan plan = await builder.PlanAsync(declaration, new() { ServerStartup = WorkspaceServerStartup.RequireExisting });
        if (plan.Actions.Count == 0 || (await server.GetSessionsAsync()).Any(session => session.Name == declaration.SessionName))
        {
            throw new InvalidOperationException("The packed workspace planner did not remain observational.");
        }
        WorkspaceResult built = await builder.ApplyAsync(plan);
        Window original = AssertSingle(built.Windows, "workspace window");
        if (original.Index != 3 || original.Panes.Count != 2 || !ReferenceEquals(original.Session, built.Session)
            || built.Session.ActiveWindow.Value.Id != original.Id
            || !built.Journal.Select(outcome => outcome.Action).SequenceEqual(plan.Actions)
            || built.Journal.Any(outcome => outcome.State != WorkspaceActionState.Completed))
        {
            throw new InvalidOperationException("The packed workspace application lost its final graph or reviewed journal.");
        }
        Console.WriteLine("workspace-plan-apply True");

        WorkspaceFile invalid = new(declaration.SessionName, windows:
        [new WorkspaceWindow("rejected", panes: [new WorkspacePane(options: new Dictionary<string, string>
        {
            ["libtmux-invalid-option"] = "fail",
        })])]);
        WorkspacePlan append = await builder.PlanAsync(invalid, new()
        {
            ExistingSession = WorkspaceExistingSession.Append,
            CompensateOnFailure = true,
        });
        try
        {
            await builder.ApplyAsync(append);
            throw new InvalidOperationException("The packed workspace accepted an invalid native option.");
        }
        catch (WorkspaceBuildException failure)
        {
            WorkspaceActionOutcome created = failure.Journal.Single(outcome => outcome.Action.Kind == WorkspaceActionKind.CreateWindow);
            WorkspaceActionOutcome rejected = failure.Journal.Single(outcome => outcome.Action.Kind == WorkspaceActionKind.SetOption);
            WorkspaceActionOutcome cleanup = AssertSingle(failure.CompensationJournal, "compensation action");
            if (failure.PartialResult?.Session.Id != built.Session.Id
                || !failure.Journal.Select(outcome => outcome.Action).SequenceEqual(append.Actions)
                || created.State != WorkspaceActionState.Completed || created.Result is not Window discarded
                || rejected.State != WorkspaceActionState.Failed || !ReferenceEquals(rejected.Failure, failure.InnerException)
                || failure.Journal[^1].State != WorkspaceActionState.NotStarted
                || cleanup.Action.Kind != WorkspaceActionKind.UnlinkWindow || cleanup.State != WorkspaceActionState.Completed
                || await server.FindWindowAsync(discarded.Id) is not null
                || AssertSingle(await built.Session.GetWindowsAsync(), "preserved workspace window").Id != original.Id)
            {
                throw new InvalidOperationException("The packed workspace lost its partial failure or removed an existing window.");
            }
        }
        Console.WriteLine("workspace-compensation True");
        return built;
    }

    private static async Task RequireNativeRejectionAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (TmuxCommandException)
        {
            return;
        }
        throw new InvalidOperationException("The packed containment guard accepted a mismatched target.");
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values, string kind) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"The package smoke expected one {kind}, but found {values.Count}.");
}
