using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Query;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Query;

[UnsupportedOSPlatform("windows")]
public sealed class QuerySourceTests
{
    private static readonly TmuxVersion Version = TmuxVersion.Parse("3.7c");
    private static readonly FieldNode Active = new(QueryTarget.Window, "window_active");
    private static readonly QueryNode Id = new ComparisonNode(QueryComparison.Equal,
        new FieldNode(QueryTarget.Window, "window_id"), new ConstantNode(new TypedIdConstant(QueryTarget.Window, "@1")));
    private static readonly QueryNode Name = new ComparisonNode(QueryComparison.Equal,
        new FieldNode(QueryTarget.Window, "window_name"), new ConstantNode(new StringConstant("editor")));

    [Fact]
    public void Exact_and_local_plans_are_pure_and_read_only()
    {
        QueryDocument document = Document(Id);
        QueryPlan<Window> exact = document.Plan<Window>(Version, QueryPushdown.Require);
        Assert.Same(document, exact.Document);
        Assert.Equal(document, exact.PushedPredicate);
        Assert.Null(exact.ResidualPredicate);
        Assert.Empty(exact.FallbackReasons);
        Assert.Equal(Version, exact.DaemonVersion);
        Assert.Equal(QueryPushdown.Require, exact.Pushdown);
        Assert.Equal(SnapshotDepth.Windows, exact.RequiredSnapshotDepth);
        Assert.Equal("window_id", Assert.Single(exact.RequiredFields).WireName);
        Assert.Throws<NotSupportedException>(() => ((IList<QueryFieldDescriptor>)exact.RequiredFields).Clear());
        QueryPlan<Window> local = document.Plan<Window>(Version, QueryPushdown.Never);
        Assert.Null(local.PushedPredicate);
        Assert.Equal(document, local.ResidualPredicate);
        Assert.NotEmpty(local.FallbackReasons);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)local.FallbackReasons).Clear());
    }

    [Fact]
    public void Only_a_leading_exact_conjunction_prefix_moves_to_tmux()
    {
        QueryPlan<Window> plan = Document(new AndNode([Id, Name, Active])).Plan<Window>(Version);
        Assert.Equal(Document(Id), plan.PushedPredicate);
        Assert.Equal(Document(new AndNode([Name, Active])), plan.ResidualPredicate);

        QueryPlan<Window> ordered = Document(new AndNode([Name, Id])).Plan<Window>(Version);
        Assert.Null(ordered.PushedPredicate);
        Assert.Equal(ordered.Document, ordered.ResidualPredicate);
        var dispatcher = new TmuxCommandDispatcher(static (_, _) => throw new InvalidOperationException("Unexpected I/O."));
        var unavailable = new Window(dispatcher, "@2");
        Assert.Throws<IncompleteSnapshotException>(() => ordered.ResidualPredicate!.Compile<Window>()(unavailable));
    }

    [Fact]
    public void Partial_disjunction_negation_and_relationships_stay_local()
    {
        foreach (QueryNode predicate in new QueryNode[]
        {
            new OrNode([Id, Name]), new NotNode(new AndNode([Id, Name])),
            new AndNode([Id, new QuantifierNode(QueryQuantifier.All,
                new FieldNode(QueryTarget.Window, "window_panes"), new ConstantNode(new BooleanConstant(true)))]),
        })
        {
            QueryPlan<Window> plan = Document(predicate).Plan<Window>(Version);
            Assert.Null(plan.PushedPredicate);
            Assert.Equal(plan.Document, plan.ResidualPredicate);
            Assert.NotEmpty(plan.FallbackReasons);
            Assert.Throws<UnsupportedQueryExpressionException>(() => plan.Document.Plan<Window>(Version, QueryPushdown.Require));
        }
        QueryPlan<Window> complete = Document(new NotNode(new OrNode([Id, Active]))).Plan<Window>(Version, QueryPushdown.Require);
        Assert.Null(complete.ResidualPredicate);
        QueryPlan<Window> graph = Document(new RelatedNode(new FieldNode(QueryTarget.Window, "window_active_pane"),
            new ConstantNode(new BooleanConstant(true)))).Plan<Window>(Version);
        Assert.Equal(SnapshotDepth.Panes, graph.RequiredSnapshotDepth);
    }

    [Fact]
    public void Invalid_types_versions_and_modes_fail_during_planning()
    {
        QueryDocument document = Document(Id);
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.Plan<Pane>(Version));
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.Plan<object>(Version));
        Assert.Throws<UnsupportedQueryExpressionException>(() => new QueryDocument(QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion, QueryTarget.Client, new ConstantNode(new BooleanConstant(true))).Plan<Client>(Version));
        Assert.Throws<ArgumentException>(() => document.Plan<Window>(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Plan<Window>(Version, (QueryPushdown)99));
        QueryPlan<Window> unknown = document.Plan<Window>(TmuxVersion.Parse("next-3.8"));
        Assert.Null(unknown.PushedPredicate);
        Assert.NotEmpty(unknown.FallbackReasons);
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.Plan<Window>(TmuxVersion.Parse("next-3.8"), QueryPushdown.Require));
    }

    [Theory]
    [InlineData("@01")]
    [InlineData("#{window_id}")]
    [InlineData("#(touch /tmp/libtmux-query-must-not-run)")]
    public void Noncanonical_typed_id_literals_never_enter_a_format(string literal)
    {
        QueryDocument document = Document(new ComparisonNode(QueryComparison.Equal,
            new FieldNode(QueryTarget.Window, "window_id"), new ConstantNode(new TypedIdConstant(QueryTarget.Window, literal))));
        QueryPlan<Window> plan = document.Plan<Window>(Version);
        Assert.Null(plan.PushedPredicate);
        Assert.Equal(document, plan.ResidualPredicate);
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.Plan<Window>(Version, QueryPushdown.Require));
    }

    [Fact]
    public void Command_budget_includes_projection_and_generation_guard()
    {
        QueryDocument document = Document(new AndNode(Enumerable.Repeat<QueryNode>(Active, 511).ToArray()));
        QueryPlan<Window> plan = document.Plan<Window>(Version);
        Assert.Null(plan.PushedPredicate);
        Assert.Equal(document, plan.ResidualPredicate);
        Assert.Contains(plan.FallbackReasons, reason => reason.Contains("budget", StringComparison.Ordinal));
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.Plan<Window>(Version, QueryPushdown.Require));
    }

    [Fact]
    public async Task Cancelled_execution_cannot_dispatch_even_when_the_source_excludes_every_row()
    {
        QueryPlan<Window> plan = Document(new ConstantNode(new BooleanConstant(false))).Plan<Window>(Version);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var server = new Server(new TmuxCommandDispatcher(static (_, _) => throw new InvalidOperationException("Unexpected I/O.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.ExecuteAsync(server, cancellation.Token));
    }

    [Fact]
    public async Task Execution_rejects_a_different_daemon_version_before_row_acquisition()
    {
        int calls = 0;
        var connection = new TmuxConnection(new ServerConnectionOptions
        {
            SocketName = "query-version-unit",
            InitializeAsync = (_, _) => throw new InvalidOperationException("Unexpected initializer."),
        }, FakeMultiplexer.AnsweringVersion((request, _) =>
        {
            calls++;
            byte[] output = System.Text.Encoding.UTF8.GetBytes("91:901\t3.2a\n");
            return Task.FromResult(new TmuxCommandResult(request.LogicalArguments, 0, output, ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(output), []));
        }, "tmux 3.7c\n"));
        var server = new Server(connection, null, null);
        QueryPlan<Window> plan = Document(Id).Plan<Window>(Version);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => plan.ExecuteAsync(server, TestContext.Current.CancellationToken));
        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
    }

    private static QueryDocument Document(QueryNode predicate) =>
        new(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion, QueryTarget.Window, predicate);
}
