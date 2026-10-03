using System.Linq.Expressions;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Query;

namespace LibTmux.IntegrationTests.Query;

// A listing tmux narrows with -f must answer exactly what a local filter over
// the whole listing answers, whatever the names contain.
[UnsupportedOSPlatform("windows")]
public sealed class PushdownDifferentialTests
{
    private static readonly string[] WindowNames =
    [
        "plain", "al,pha", "br}ace", "op{en", "has#hash", "##double", "#[fg=red]style",
        "glob*star", "q?mark", "br[ack]et", "back\\slash", "uni-é-漢字", "colon:x",
        "  spaced  ", "trail#", "#{session_name}", "a;b", "UPPER", "ı-dotless", "#,#}", "\\*",
    ];

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Pushed_window_filters_answer_what_local_filters_answer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        string marker = Path.Combine(Path.GetTempPath(), "libtmux-pushdown-" + Guid.NewGuid().ToString("N"));
        string[] names = [.. WindowNames, $"#(touch {marker})"];

        // A shell starting in a window would otherwise rename it mid-test.
        await raw.ExecuteAsync(["set-option", "-g", "automatic-rename", "off"], token);
        await raw.ExecuteAsync(["rename-window", "-t", "@0", "first"], token);
        foreach (string name in names)
        {
            // rename-window expands formats, so doubling # stores the text.
            RawTmuxResult created = await raw.ExecuteAsync(
                ["new-window", "-d", "-P", "-F", "#{window_id}", "-t", "$0"],
                token);
            string literal = name
                .Replace("#", "##", StringComparison.Ordinal)
                .Replace("##[", "#[", StringComparison.Ordinal);
            await raw.ExecuteAsync(["rename-window", "-t", created.StandardOutputText.Trim(), "--", literal], token);
        }

        Server server = await ConnectAsync(raw, token);
        IReadOnlyList<Window> all = await server.GetWindowsAsync(token);
        Assert.Equal(names.Length + 1, all.Count);
        List<string> disagreements = [];

        // tmux must keep every local match, and exactly the local matches when
        // the filter is exact. One list-windows evaluates a column per filter.
        QueryDocument[] documents =
        [
            .. Predicates(names, all[^1].Id)
                .Select(QueryExtensions.Translate)
                .Where(document => TmuxFilterRenderer.Superset(document) is not null),
        ];
        Assert.True(documents.Length > 100, "Too few predicates reached tmux.");
        foreach (QueryDocument[] chunk in documents.Chunk(32))
        {
            string format = "#{window_id}" + string.Concat(
                chunk.Select(document => "\t#{?" + TmuxFilterRenderer.Superset(document) + ",1,0}"));
            RawTmuxResult listed = await raw.ExecuteAsync(["list-windows", "-a", "-F", format], token);
            Dictionary<string, string[]> columns = listed.StandardOutputLines
                .Select(line => line.Split('\t'))
                .ToDictionary(cells => cells[0], cells => cells[1..], StringComparer.Ordinal);
            for (int index = 0; index < chunk.Length; index++)
            {
                Func<Window, bool> local = chunk[index].Compile<Window>();
                bool exact = TmuxFilterRenderer.IsExact(chunk[index]);
                string[] wrong =
                [
                    .. all.Where(window =>
                        {
                            bool kept = columns[window.Id.ToString()][index] == "1";
                            return local(window) ? !kept : exact && kept;
                        })
                        .Select(Key),
                ];
                if (wrong.Length > 0)
                {
                    disagreements.Add($"{TmuxFilterRenderer.Superset(chunk[index])}: [{string.Join("|", wrong)}]");
                }
            }
        }

        QueryDocument hashed = QueryExtensions.Translate<Window>(window => window.Name.StartsWith("##", StringComparison.Ordinal));
        Assert.Equal(
            all.Where(hashed.Compile<Window>()).Select(Key),
            (await server.QueryAsync<Window>(new ListingRequest(QueryTarget.Window, Filter: hashed), token)).Select(Key));
        Assert.False(File.Exists(marker), "A filter operand ran a shell command.");
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Relations_scopes_and_screens_answer_what_a_snapshot_answers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        string ready = "pushdown-" + Guid.NewGuid().ToString("N");
        await raw.ExecuteAsync(["set-option", "-g", "automatic-rename", "off"], token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "ops", "-n", "logs", "sh"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-t", "ops:logs", "sh"], token);
        await raw.ExecuteAsync(
            [
                "new-window", "-d", "-t", "ops", "-n", "edit",
                $"printf 'NEEDLE a,b}}c#d[x]*? end\\n'; '{raw.TmuxBinaryPath}' wait-for -S {ready}; exec sleep 600",
            ],
            token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "dev", "-n", "edit", "sh"], token);
        await raw.ExecuteAsync(["wait-for", ready], token);
        Server server = await ConnectAsync(raw, token);
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        SessionId ops = snapshot.Sessions.Single(session => session.Name == "ops").Id;
        PaneId first = snapshot.Panes[0].Id;
        List<string> disagreements = [];

        async Task Agree<T>(
            Expression<Func<T, bool>> predicate,
            IEnumerable<T> universe,
            ListingRequest request,
            Func<T, string> key)
        {
            QueryDocument document = QueryExtensions.Translate(predicate);
            string[] expected = [.. universe.Where(document.Compile<T>()).Select(key)];
            string[] actual = [.. (await server.QueryAsync<T>(request with { Filter = document }, token)).Select(key)];
            if (!expected.SequenceEqual(actual))
            {
                disagreements.Add(
                    $"{predicate.Body}: expected [{string.Join("|", expected)}], got [{string.Join("|", actual)}]");
            }
        }

        ListingRequest sessions = new(QueryTarget.Session);
        ListingRequest windows = new(QueryTarget.Window);
        ListingRequest opsWindows = new(QueryTarget.Window, Session: ops);
        ListingRequest opsPanes = new(QueryTarget.Pane, Session: ops);
        await Agree<Session>(s => s.Attached, snapshot.Sessions, sessions, s => s.Name);
        await Agree<Session>(s => !s.Attached && s.Id != ops, snapshot.Sessions, sessions, s => s.Name);
        await Agree<Session>(s => s.Windows.Any(w => w.Name == "edit"), snapshot.Sessions, sessions, s => s.Name);
        await Agree<Session>(
            s => s.Windows.All(w => w.Name.StartsWith("ed", StringComparison.Ordinal)),
            snapshot.Sessions,
            sessions,
            s => s.Name);
        await Agree<Session>(
            s => !s.Windows.Any(w => w.Panes.Any(p => p.CurrentCommand == "sh")),
            snapshot.Sessions,
            sessions,
            s => s.Name);
        await Agree<Window>(
            w => w.Panes.Any(p => p.Id != first) && w.Name == "logs",
            snapshot.Windows,
            windows,
            w => w.Id.ToString());
        await Agree<Window>(
            w => w.Name == "edit",
            snapshot.Windows.Where(w => w.Edge.SessionId == ops),
            opsWindows,
            w => w.Id.ToString());
        await Agree<Window>(
            w => w.Panes.Any(p => p.CurrentCommand == "sleep"),
            snapshot.Windows.Where(w => w.Edge.SessionId == ops),
            opsWindows,
            w => w.Id.ToString());
        await Agree<Pane>(p => p.Id == first, snapshot.Panes, new ListingRequest(QueryTarget.Pane), p => p.Id.ToString());
        await Agree<Pane>(
            p => p.CurrentCommand != "sh",
            snapshot.Panes.Where(p => p.Window.Edge.SessionId == ops),
            opsPanes,
            p => p.Id.ToString());

        // A raw filter selects among the target's own rows, also beside a
        // relation filter that captures whole sessions.
        await Agree<Window>(
            w => w.Panes.Any(p => p.Id != first),
            snapshot.Windows.Where(w => w.Name == "logs"),
            windows with { Unsafe = new UnsafeTmuxFilter("#{==:#{window_name},logs}") },
            w => w.Id.ToString());
        await Agree<Session>(
            s => s.Windows.Any(w => w.Name == "edit"),
            snapshot.Sessions.Where(s => s.Name == "dev"),
            sessions with { Unsafe = new UnsafeTmuxFilter("#{==:#{session_name},dev}") },
            s => s.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => server.QueryAsync<Window>(
            new ListingRequest(
                QueryTarget.Window,
                Filter: QueryExtensions.Translate<Window>(w => w.Name == "edit"),
                Unsafe: new UnsafeTmuxFilter("1,0")),
            token));

        string sleeper = snapshot.Panes.Single(pane => pane.CurrentCommand == "sleep").Id.ToString();
        (PaneScreenSearch Search, bool Found)[] searches =
        [
            (new("a,b}c#d[x]*?", IsPattern: false, IgnoreCase: false), true),
            (new("a*d", IsPattern: false, IgnoreCase: false), false),
            (new("needle A,B", IsPattern: false, IgnoreCase: true), true),
            (new("needle", IsPattern: false, IgnoreCase: false), false),
            (new("NE+DLE [a-z],b", IsPattern: true, IgnoreCase: false), true),
            (new("c[#][d]", IsPattern: true, IgnoreCase: false), true),
        ];
        Assert.Throws<ArgumentException>(() => new PaneScreenSearch("c#[d]", IsPattern: true, IgnoreCase: false).Render());
        foreach ((PaneScreenSearch search, bool found) in searches)
        {
            string[] matched =
            [
                .. (await server.QueryAsync<Pane>(new ListingRequest(QueryTarget.Pane, Screen: search), token))
                    .Select(pane => pane.Id.ToString()),
            ];
            if (!matched.SequenceEqual(found ? [sleeper] : []))
            {
                disagreements.Add($"{search.Render()}: got [{string.Join("|", matched)}]");
            }
        }

        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    [UnixFact]
    public async Task Pane_geometry_titles_paths_and_window_sizes_answer_what_a_snapshot_answers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);

        // A shell starting in a window would otherwise rename it mid-test.
        await raw.ExecuteAsync(["set-option", "-g", "automatic-rename", "off"], token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "geo", "-n", "grid", "-x", "120", "-y", "40", "sh"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-h", "-t", "geo:grid", "-c", Path.GetTempPath(), "sh"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-v", "-t", "geo:grid.0", "sh"], token);
        await raw.ExecuteAsync(["select-pane", "-t", "geo:grid.1", "-T", "build"], token);
        await raw.ExecuteAsync(["select-pane", "-t", "geo:grid.2", "-T", "b#uild,x}"], token);
        Server server = await ConnectAsync(raw, token);
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Pane[] panes = [.. snapshot.Panes];
        Window[] windows = [.. snapshot.Windows];
        string path = panes.Single(pane => pane.Left > 0).CurrentPath!;
        List<string> disagreements = [];

        async Task Agree<T>(Expression<Func<T, bool>> predicate, IEnumerable<T> universe, QueryTarget target, Func<T, string> key)
        {
            QueryDocument document = QueryExtensions.Translate(predicate);
            string[] expected = [.. universe.Where(document.Compile<T>()).Select(key)];
            string[] actual = [.. (await server.QueryAsync<T>(new ListingRequest(target, Filter: document), token)).Select(key)];
            if (!expected.SequenceEqual(actual))
            {
                disagreements.Add($"{predicate.Body}: expected [{string.Join("|", expected)}], got [{string.Join("|", actual)}]");
            }
        }

        string PaneKey(Pane pane) => pane.Id.ToString();
        await Agree<Pane>(pane => pane.Width > 60, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Height <= 20, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Left == 0 && pane.Top > 0, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Index != 1, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.AtTop && !pane.AtBottom, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.AtLeft || pane.AtRight, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Title == "build", panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Title!.StartsWith("b#uild,", StringComparison.Ordinal), panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.CurrentPath == path, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.CurrentPath!.Contains("tmp", StringComparison.Ordinal), panes, QueryTarget.Pane, PaneKey);
        await Agree<Window>(window => window.Index == 0 && window.Width >= 120, windows, QueryTarget.Window, Key);
        await Agree<Window>(window => window.Height < 40, windows, QueryTarget.Window, Key);

        // Escaped operands inside tmux's window and pane loops.
        Session[] sessions = [.. snapshot.Sessions];
        string SessionKey(Session session) => session.Id.ToString();
        await Agree<Session>(
            session => session.Windows.Any(window => window.Panes.Any(pane => pane.Title == "b#uild,x}")),
            sessions,
            QueryTarget.Session,
            SessionKey);
        await Agree<Session>(
            session => session.Windows.Any(window => window.Panes.Any(pane => pane.CurrentPath == path)),
            sessions,
            QueryTarget.Session,
            SessionKey);

        Assert.True(panes.Length >= 3 && path.Length > 0);
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    [UnixFact]
    public async Task Pane_and_window_state_flags_answer_what_a_snapshot_answers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await raw.ExecuteAsync(["set-option", "-g", "automatic-rename", "off"], token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "flags", "-n", "modes", "-x", "120", "-y", "40", "sh"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-t", "flags:modes", "sh"], token);
        await raw.ExecuteAsync(["copy-mode", "-t", "flags:modes.0"], token);

        // tmux counts stacked modes, so this pane reports 2 and must still
        // count as in a mode.
        await raw.ExecuteAsync(["copy-mode", "-t", "flags:modes.1"], token);
        await raw.ExecuteAsync(["clock-mode", "-t", "flags:modes.1"], token);
        await raw.ExecuteAsync(["new-window", "-d", "-t", "flags", "-n", "zoom", "sh"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-t", "flags:zoom", "sh"], token);
        await raw.ExecuteAsync(["resize-pane", "-Z", "-t", "flags:zoom.0"], token);
        await raw.ExecuteAsync(["new-window", "-d", "-t", "flags", "-n", "dead", "sh"], token);
        await raw.ExecuteAsync(["set-option", "-w", "-t", "flags:dead", "remain-on-exit", "on"], token);
        await raw.ExecuteAsync(["set-hook", "-g", "pane-died", "wait-for -S died"], token);
        await raw.ExecuteAsync(["split-window", "-d", "-t", "flags:dead", "true"], token);
        await raw.ExecuteAsync(["wait-for", "died"], token);
        Server server = await ConnectAsync(raw, token);
        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Pane[] panes = [.. snapshot.Panes];
        Window[] windows = [.. snapshot.Windows];
        Session[] sessions = [.. snapshot.Sessions];
        int pid = panes.First(pane => !pane.Dead).ProcessId;
        List<string> disagreements = [];

        async Task Agree<T>(Expression<Func<T, bool>> predicate, IEnumerable<T> universe, QueryTarget target, Func<T, string> key)
        {
            QueryDocument document = QueryExtensions.Translate(predicate);
            string[] expected = [.. universe.Where(document.Compile<T>()).Select(key)];
            string[] actual = [.. (await server.QueryAsync<T>(new ListingRequest(target, Filter: document), token)).Select(key)];
            if (!expected.SequenceEqual(actual))
            {
                disagreements.Add($"{predicate.Body}: expected [{string.Join("|", expected)}], got [{string.Join("|", actual)}]");
            }
        }

        string PaneKey(Pane pane) => pane.Id.ToString();
        await Agree<Pane>(pane => pane.Active, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => !pane.Active && !pane.InMode, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.InMode, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.Dead, panes, QueryTarget.Pane, PaneKey);
        await Agree<Pane>(pane => pane.ProcessId == pid, panes, QueryTarget.Pane, PaneKey);
        await Agree<Window>(window => window.Active, windows, QueryTarget.Window, Key);
        await Agree<Window>(window => window.Zoomed && !window.Active, windows, QueryTarget.Window, Key);
        await Agree<Session>(
            session => session.Windows.Any(window => window.Zoomed),
            sessions,
            QueryTarget.Session,
            session => session.Id.ToString());

        Assert.Contains(panes, pane => pane.RawFormatFields["pane_in_mode"] == "2");
        Assert.Equal(2, panes.Count(pane => pane.InMode));
        Assert.Single(panes, pane => pane.Dead);
        Assert.Single(windows, window => window.Zoomed);
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    // Wall time is too noisy to gate on; the tmux processes a query starts and
    // the rows tmux returns are exact, so a pushdown that silently fell back to
    // a full read fails here.
    [UnixFact]
    public async Task A_pushed_down_listing_reads_only_matching_rows_in_as_few_tmux_processes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await raw.ExecuteAsync(["set-option", "-g", "automatic-rename", "off"], token);
        foreach (string name in new[] { "alpha", "beta", "gamma" })
        {
            await raw.ExecuteAsync(["new-session", "-d", "-s", name, "-n", name == "beta" ? "target" : "other", "sh"], token);
        }

        int processes = 0;
        int rows = 0;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                Interceptor = async (_, next, cancellationToken) =>
                {
                    Interlocked.Increment(ref processes);
                    TmuxCommandResult result = await next(cancellationToken);
                    Interlocked.Add(ref rows, result.StandardOutputLines.Count);
                    return result;
                },
            },
            token);
        _ = await server.GetSessionsAsync(token);

        async Task<(int Processes, int Rows, string[] Names)> CountAsync<T>(ListingRequest request, Func<T, string> name)
        {
            Interlocked.Exchange(ref processes, 0);
            Interlocked.Exchange(ref rows, 0);
            IReadOnlyList<T> found = await server.QueryAsync<T>(request, token);
            return (Volatile.Read(ref processes), Volatile.Read(ref rows), [.. found.Select(name)]);
        }

        QueryDocument windowNamed = QueryExtensions.Translate<Window>(window => window.Name == "target");
        QueryDocument sessionWithTarget = QueryExtensions.Translate<Session>(
            session => session.Windows.Any(window => window.Name == "target"));

        (int windowProcesses, int windowRows, string[] windows) = await CountAsync<Window>(
            new ListingRequest(QueryTarget.Window, Filter: windowNamed),
            window => window.Name);
        (int relationProcesses, int relationRows, string[] sessions) = await CountAsync<Session>(
            new ListingRequest(QueryTarget.Session, Filter: sessionWithTarget),
            session => session.Name);

        Assert.Equal(["target"], windows);
        Assert.Equal(["beta"], sessions);
        Assert.Equal((1, 2), (windowProcesses, windowRows));
        Assert.Equal((3, 6), (relationProcesses, relationRows));
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);

    private static string Key(Window window) => window.Id + "=" + window.Name;

    private static IEnumerable<Expression<Func<Window, bool>>> Predicates(string[] names, WindowId last)
    {
        foreach (string name in names)
        {
            string head = name[..(name.Length / 2)];
            string tail = name[(name.Length / 2)..];
            string middle = name.Length > 2 ? name[1..^1] : name;
            yield return Equal(name);
            yield return Not(Equal(name));
            yield return StartsWith(head);
            yield return EndsWith(tail);
            yield return Not(Contains(middle));
            yield return Not(Both(StartsWith(head), Not(Equal(name))));
            yield return Either(Equal(name), Both(EndsWith(tail), Not(Id(last))));
        }

        yield return StartsWith(string.Empty);
        yield return Not(Contains(string.Empty));
        yield return Equal(string.Empty);
    }

    private static Expression<Func<Window, bool>> Equal(string value) => window => window.Name == value;

    private static Expression<Func<Window, bool>> StartsWith(string value) =>
        window => window.Name.StartsWith(value, StringComparison.Ordinal);

    private static Expression<Func<Window, bool>> EndsWith(string value) =>
        window => window.Name.EndsWith(value, StringComparison.Ordinal);

    private static Expression<Func<Window, bool>> Contains(string value) =>
        window => window.Name.Contains(value, StringComparison.Ordinal);

    private static Expression<Func<Window, bool>> Id(WindowId value) => window => window.Id == value;

    private static Expression<Func<Window, bool>> Not(Expression<Func<Window, bool>> operand) =>
        Expression.Lambda<Func<Window, bool>>(Expression.Not(operand.Body), operand.Parameters);

    private static Expression<Func<Window, bool>> Both(
        Expression<Func<Window, bool>> left,
        Expression<Func<Window, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<Window, bool>> Either(
        Expression<Func<Window, bool>> left,
        Expression<Func<Window, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    private static Expression<Func<Window, bool>> Combine(
        Expression<Func<Window, bool>> left,
        Expression<Func<Window, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine)
    {
        ParameterExpression parameter = left.Parameters[0];
        Expression body = new Rebind(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<Window, bool>>(combine(left.Body, body), parameter);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
