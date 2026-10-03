using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.Query;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Compares a query tmux narrows with -f against reading everything and filtering locally.</summary>
/// <remarks>
/// The server holds 64 sessions of 4 windows; one pane in 64 runs <c>tail</c>.
/// Every route must return the same panes, or sessions, before anything is timed.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(ModeWorkloadBenchmarkConfig))]
public class FSharpQueryPushdownBenchmarks
{
    private const int Sessions = 64;
    private const int WindowsPerSession = 4;
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(120);

    private TemporaryServerScope? _scope;
    private Server _server = null!;
    private LibTmux.FSharp.Filter<Pane> _tailPanes = null!;
    private LibTmux.FSharp.Filter<Session> _sessionsWithTail = null!;
    private QueryDocument _tailDocument = null!;
    private QueryDocument _sessionDocument = null!;

    /// <summary>Selects how the matching panes or sessions are found.</summary>
    [Params("pushdown", "list-then-filter", "snapshot-then-filter")]
    public string Route { get; set; } = null!;

    /// <summary>Builds the server and checks that every route agrees.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        using var setup = new CancellationTokenSource(SetupBudget);
        CancellationToken cancellationToken = setup.Token;
        var options = new TmuxTestOptions(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"lt-pushdown-{Guid.NewGuid():N}"[..24],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX"] = null, ["TMUX_PANE"] = null },
        });
        _scope = await new TmuxTestFactory().CreateServerAsync(options, cancellationToken).ConfigureAwait(false);
        _server = _scope.Server;

        // One chain per session keeps setup to 64 tmux processes.
        for (int session = 0; session < Sessions; session++)
        {
            TmuxChain chain = _server.Chain().Then(TmuxCommand.Create(
                "new-session", "-d", "-s", $"s{session:D2}", "-n", "w0", "sleep 3600"));
            for (int window = 1; window < WindowsPerSession; window++)
            {
                string command = window == 1 && session % 16 == 0 ? "tail -f /dev/null" : "sleep 3600";
                chain = chain.Then(TmuxCommand.Create(
                    "new-window", "-d", "-t", $"s{session:D2}", "-n", $"w{window}", command));
            }

            await chain.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        _tailPanes = LibTmux.FSharp.Filter.eq("tail", LibTmux.FSharp.PaneFields.currentCommand);
        _sessionsWithTail = LibTmux.FSharp.Filter.any(
            LibTmux.FSharp.SessionFields.windows,
            LibTmux.FSharp.Filter.any(LibTmux.FSharp.WindowFields.panes, _tailPanes));
        _tailDocument = LibTmux.FSharp.Filter.toDocument(_tailPanes);
        _sessionDocument = LibTmux.FSharp.Filter.toDocument(_sessionsWithTail);

        // BenchmarkDotNet sets the route before setup; every route is checked
        // against the others, then the selected one is put back.
        string selected = Route;
        string[] routes = ["pushdown", "list-then-filter", "snapshot-then-filter"];
        string? expectedPanes = null;
        string? expectedSessions = null;
        foreach (string route in routes)
        {
            Route = route;
            string panes = string.Join(",", (await FindTailPanes().ConfigureAwait(false)).Select(pane => pane.Id));
            string sessions = string.Join(",", (await FindSessionsWithTail().ConfigureAwait(false)).Select(session => session.Id));
            expectedPanes ??= panes;
            expectedSessions ??= sessions;
            if (panes != expectedPanes || sessions != expectedSessions || panes.Length == 0 || sessions.Length == 0)
            {
                throw new InvalidOperationException($"Route {route} disagreed: [{panes}] [{sessions}].");
            }
        }

        Route = selected;
    }

    /// <summary>Stops the benchmark server.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_scope is not null)
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Finds the panes running <c>tail</c>.</summary>
    /// <returns>The matching panes.</returns>
    [Benchmark]
    public Task<IReadOnlyList<Pane>> FindTailPanes() => Route switch
    {
        "pushdown" => LibTmux.FSharp.Query.list(
            CancellationToken.None,
            LibTmux.FSharp.Query.where(_tailPanes, LibTmux.FSharp.Server.panes(_server))),
        "list-then-filter" => ListThenFilter(),
        _ => SnapshotThenFilter(),
    };

    /// <summary>Finds the sessions with a window whose pane runs <c>tail</c>.</summary>
    /// <remarks>A relation needs a snapshot to filter locally, so both local routes capture.</remarks>
    /// <returns>The matching sessions.</returns>
    [Benchmark]
    public Task<IReadOnlyList<Session>> FindSessionsWithTail() => Route switch
    {
        "pushdown" => LibTmux.FSharp.Query.list(
            CancellationToken.None,
            LibTmux.FSharp.Query.where(_sessionsWithTail, LibTmux.FSharp.Server.sessions(_server))),
        _ => SnapshotSessions(),
    };

    private async Task<IReadOnlyList<Pane>> ListThenFilter() =>
        (await _server.GetPanesAsync().ConfigureAwait(false)).Matching(_tailDocument);

    private async Task<IReadOnlyList<Pane>> SnapshotThenFilter() =>
        (await _server.CaptureSnapshotAsync(SnapshotDepth.Panes).ConfigureAwait(false)).Panes.Matching(_tailDocument);

    private async Task<IReadOnlyList<Session>> SnapshotSessions() =>
        (await _server.CaptureSnapshotAsync(SnapshotDepth.Panes).ConfigureAwait(false)).Sessions.Matching(_sessionDocument);
}
