using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class QueryCompilationTests
{
    private sealed record Row(string SessionName);

    private sealed record SessionCountRow(long SessionWindows);

    private sealed record MissingNameRow(string Other);

    private sealed record IncompatibleNameRow(int SessionName);

    private sealed class WriteOnlyNameRow
    {
        public string SessionName { private get; set; } = string.Empty;
    }

    [Fact]
    public void Compilation_rejects_a_missing_projection_member()
    {
        QueryDocument document = QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");

        UnsupportedQueryExpressionException error =
            Assert.Throws<UnsupportedQueryExpressionException>(document.Compile<MissingNameRow>);

        Assert.Contains("session_name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compilation_binds_a_reused_member_and_regex_once()
    {
        FieldNode field = new(QueryTarget.Session, "session_name");
        RegexNode regex = new(
            field,
            QueryRegexSemantics.Dialect,
            "^build",
            RegexOptions.CultureInvariant);
        QueryDocument document = new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            QueryTarget.Session,
            new AndNode([regex, regex]));

        Func<Row, bool> predicate = QueryInterpreter.Compile<Row>(
            document,
            out QueryBindingMetrics metrics);

        Assert.Equal(new QueryBindingMetrics(1, 1), metrics);
        Assert.True(predicate(new Row("build-one")));
        Assert.False(predicate(new Row("other")));
    }

    [Fact]
    public void Compilation_rejects_unreadable_and_incompatible_projection_members()
    {
        QueryDocument document = QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");

        Assert.Throws<UnsupportedQueryExpressionException>(document.Compile<WriteOnlyNameRow>);
        Assert.Throws<UnsupportedQueryExpressionException>(document.Compile<IncompatibleNameRow>);
    }

    [Fact]
    public void Relation_fields_read_counts_from_captured_entities()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new InvalidOperationException("No command expected."));
        Window[] windows =
        [
            new Window(dispatcher, "@1"),
            new Window(dispatcher, "@2"),
        ];
        var session = new Session(dispatcher, "$1").WithCaptured(
            CapturedRelation.Capture(windows, "windows", SnapshotDepth.Windows),
            CapturedRelation.Capture<Pane>([], "panes", SnapshotDepth.Panes));
        QueryDocument sessions = QueryExtensions.Translate<SessionCountRow>(
            row => row.SessionWindows > 1);

        Assert.True(sessions.Compile<Session>()(session));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Entity_compilation_binds_catalog_accessors_and_relations()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new InvalidOperationException("No command expected."));
        var generation = new ServerGeneration(95, 905);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "query-captured-windows" },
            (_, _) => throw new InvalidOperationException("No command expected."));
        var owner = new Server(connection, generation, "tmux 3.7");
        Window Capture(int id) => new(owner, connection, generation, new WindowId(id),
            new Dictionary<string, string?>());
        var session = new Session(dispatcher, "$1").WithCaptured(
            CapturedRelation.Capture([Capture(1), Capture(2)], "windows", SnapshotDepth.Windows),
            CapturedRelation.Capture<Pane>([], "panes", SnapshotDepth.Panes));
        Assert.NotEqual(session.Windows[0].Id, session.Windows[1].Id);
        WindowId captured = session.Windows[0].Id;
        var absent = new WindowId(captured.Value + 9);
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.True(QueryInterpreter.CompileEntity<Session>(
            QueryExtensions.Translate<SessionCountRow>(row => row.SessionWindows > 1), token)(session));
        Assert.False(QueryInterpreter.CompileEntity<Session>(
            QueryExtensions.Translate<Session>(candidate => candidate.Windows.All(window => window.Id == captured)), token)(session));
        Assert.False(QueryInterpreter.CompileEntity<Session>(
            QueryExtensions.Translate<Session>(candidate => candidate.Windows.Any(window => window.Id == absent)), token)(session));
    }

    [Fact]
    public void Entity_compilation_refuses_a_projection()
    {
        QueryDocument document = QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");

        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryInterpreter.CompileEntity<Row>(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Pane_query_fields_distinguish_uncaptured_title_and_edge_flags()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new InvalidOperationException("A captured query reached tmux."));
        var uncaptured = new Pane(dispatcher, "%0");
        var generation = new ServerGeneration(94, 904);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "query-captured-pane" },
            (_, _) => throw new InvalidOperationException("A captured query reached tmux."));
        var server = new Server(connection, generation, "tmux 3.7");
        Pane Capture(IReadOnlyDictionary<string, string?> fields) =>
            new(server, connection, generation, new PaneId(0), fields);

        QueryDocument title = QueryExtensions.Translate<Pane>(pane => pane.Title == null);
        Func<Pane, bool> isTitleNull = QueryInterpreter.CompileEntity<Pane>(title, TestContext.Current.CancellationToken);
        Assert.Throws<IncompleteSnapshotException>(() => isTitleNull(uncaptured));
        Assert.Throws<IncompleteSnapshotException>(() => isTitleNull(Capture(new Dictionary<string, string?>())));
        Assert.True(isTitleNull(Capture(new Dictionary<string, string?> { ["pane_title"] = null })));
        Assert.False(isTitleNull(Capture(new Dictionary<string, string?> { ["pane_title"] = string.Empty })));

        foreach (string wireName in new[] { "pane_at_top", "pane_at_bottom", "pane_at_left", "pane_at_right" })
        {
            QueryDocument edge = new(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion,
                QueryTarget.Pane, new NotNode(new FieldNode(QueryTarget.Pane, wireName)));
            Func<Pane, bool> notAtEdge = QueryInterpreter.CompileEntity<Pane>(edge, TestContext.Current.CancellationToken);
            Assert.Throws<IncompleteSnapshotException>(() => notAtEdge(uncaptured));
            Assert.Throws<IncompleteSnapshotException>(() => notAtEdge(Capture(new Dictionary<string, string?>())));
            Assert.Throws<IncompleteSnapshotException>(() => notAtEdge(Capture(
                new Dictionary<string, string?> { [wireName] = null })));
            Assert.True(notAtEdge(Capture(new Dictionary<string, string?> { [wireName] = "0" })));
            Assert.False(notAtEdge(Capture(new Dictionary<string, string?> { [wireName] = "1" })));
            Assert.Throws<TmuxProtocolException>(() => notAtEdge(Capture(
                new Dictionary<string, string?> { [wireName] = "2" })));
        }
    }
}
