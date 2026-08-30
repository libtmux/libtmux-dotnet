using System.Runtime.Versioning;
using System.Text;
using LibTmux.Query;
using LibTmux.Query.Json;
using LibTmux.Testing;
using LibTmux.Workspace;

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
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"libtmux-pkg-{Guid.NewGuid():N}"[..24],
            configurationFile: "/dev/null"));

        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(options);

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
        return 0;
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values, string kind) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"The psmux package smoke expected one {kind}, but found {values.Count}.");
}
