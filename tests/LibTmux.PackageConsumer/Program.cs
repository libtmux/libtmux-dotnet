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

        Window graph = await scope.Session.CreateWindowAsync(new NewWindowRequest
        {
            Name = "package-graph",
            StartDirectory = Path.GetTempPath(),
            Command = "exec /bin/cat",
        });
        Session other = await server.CreateSessionAsync(new NewSessionRequest
        {
            Name = "package-other",
            Command = "exec /bin/cat",
            ExpectedGeneration = inspected.Generation,
        });
        await graph.LinkAsync(new LinkWindowRequest(other.Id.ToString())
        {
            TargetIndex = "5",
            Detach = true,
        });
        Window placement = (await other.GetWindowsAsync()).Single(window => window.Index == 5);
        await placement.SelectAsync();
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes);
        await scope.DisposeAsync();

        string path = snapshot.Panes.First(pane => pane.Window.Id == graph.Id).CurrentPath
            ?? throw new InvalidOperationException("The graph fixture did not capture its working directory.");
        QueryDocument pathQuery = QueryExtensions.Translate<Pane>(pane => pane.CurrentPath == path);
        IReadOnlyList<Pane> pathMatches = snapshot.Panes.Matching(QueryJson.Deserialize(QueryJson.Serialize(pathQuery)));
        if (!pathMatches.SequenceEqual(snapshot.Panes.Where(pane => pane.CurrentPath == path))
            || !pathMatches.Any(pane => pane.Window.Id == graph.Id))
        {
            throw new InvalidOperationException("The packed CurrentPath filter disagreed with captured native data.");
        }
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

    private static T AssertSingle<T>(IReadOnlyList<T> values, string kind) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"The psmux package smoke expected one {kind}, but found {values.Count}.");
}
