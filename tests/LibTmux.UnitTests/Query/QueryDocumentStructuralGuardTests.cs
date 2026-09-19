using System.Text.RegularExpressions;
using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class QueryDocumentStructuralGuardTests
{
    private const int VersionOneMaximumDepth = 32;
    private const int VersionOneMaximumNodeOccurrences = 512;
    private static readonly QueryNode True = new ConstantNode(new BooleanConstant(true));

    private sealed record Row(string SessionName);

    private sealed record UnknownNode : QueryNode;

    private sealed record UnknownConstant : QueryConstant;

    // Keyed by edge rather than carrying the node: QueryNode is internal to
    // LibTmux, and a public theory parameter cannot name it.
    public static TheoryData<string> NodesWithDeepChildren =>
    [
        "and operand",
        "or operand",
        "not operand",
        "comparison left",
        "comparison right",
        "string left",
        "string right",
        "regex input",
        "quantifier predicate",
    ];

    private static QueryNode DeepChild(string edge)
    {
        QueryNode deep = NestedNot(True, VersionOneMaximumDepth - 1);
        return edge switch
        {
            "and operand" => new AndNode([deep]),
            "or operand" => new OrNode([deep]),
            "not operand" => new NotNode(deep),
            "comparison left" => new ComparisonNode(QueryComparison.Equal, deep, True),
            "comparison right" => new ComparisonNode(QueryComparison.Equal, True, deep),
            "string left" => new StringNode(QueryStringOperation.EqualsOrdinal, deep, True),
            "string right" => new StringNode(QueryStringOperation.EqualsOrdinal, True, deep),
            "regex input" => new RegexNode(deep, QueryRegexSemantics.Dialect, "x", RegexOptions.None),
            "quantifier predicate" => new QuantifierNode(
                QueryQuantifier.Any,
                new FieldNode(QueryTarget.Session, "session_windows"),
                deep),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown edge."),
        };
    }

    public static TheoryData<string, string> MalformedShapes =>
        new()
        {
            { "root", "null" },
            { "quantifier relation", "null" },
            { "constant value", "null" },
            { "unknown node", "not supported" },
            { "unknown constant", "not supported" },
        };

    private static QueryNode Malformed(string shape) => shape switch
    {
        "root" => null!,
        "quantifier relation" => new QuantifierNode(QueryQuantifier.Any, null!, True),
        "constant value" => new ConstantNode(null!),
        "unknown node" => new UnknownNode(),
        "unknown constant" => new ConstantNode(new UnknownConstant()),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
    };

    [Fact]
    public void Depth_limit_accepts_the_boundary_and_rejects_the_next_level()
    {
        QueryDocument atLimit = Document(
            NestedNot(True, VersionOneMaximumDepth - 1));

        Assert.NotNull(atLimit.Compile<Row>());
        Assert.Equal(SnapshotDepth.Sessions, atLimit.RequiredSnapshotDepth);
        AssertRejected(new NotNode(atLimit.Predicate), "nesting depth");
    }

    [Fact]
    public void Size_limit_counts_shared_nodes_as_occurrences()
    {
        QueryDocument atLimit = Document(
            new OrNode(
                [.. Enumerable.Repeat(True, VersionOneMaximumNodeOccurrences - 1)]));

        Assert.NotNull(atLimit.Compile<Row>());
        Assert.Equal(SnapshotDepth.Sessions, atLimit.RequiredSnapshotDepth);

        AssertRejected(
            new OrNode(
                [.. Enumerable.Repeat(True, VersionOneMaximumNodeOccurrences)]),
            "node count");
    }

    [Theory]
    [MemberData(nameof(NodesWithDeepChildren))]
    public void Guard_visits_every_query_node_edge(string edge) =>
        AssertRejected(DeepChild(edge), "nesting depth");

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public void Entry_points_reject_malformed_shapes(string shape, string messageFragment) =>
        AssertRejected(Malformed(shape), messageFragment);

    [Fact]
    public void Cancellation_is_checked_during_the_structural_walk()
    {
        QueryDocument document = Document(new AndNode([True, True, True, True]));
        int checks = 0;

        Assert.Throws<OperationCanceledException>(
            () => QueryDocumentValidator.Validate(
                document,
                () =>
                {
                    if (++checks == 3)
                    {
                        throw new OperationCanceledException();
                    }
                }));

        Assert.Equal(3, checks);
    }

    [Fact]
    public void Snapshot_depth_rejects_semantically_invalid_documents()
    {
        QueryDocument[] malformed =
        [
            new("someone-else", QueryDocument.CurrentVersion, QueryTarget.Session, True),
            new(
                QueryDocument.CurrentSchema,
                QueryDocument.CurrentVersion + 1,
                QueryTarget.Session,
                True),
            new(
                QueryDocument.CurrentSchema,
                QueryDocument.CurrentVersion,
                (QueryTarget)int.MaxValue,
                True),
            new(
                QueryDocument.CurrentSchema,
                QueryDocument.CurrentVersion,
                QueryTarget.Session,
                new FieldNode(QueryTarget.Session, "unknown_field")),
        ];

        Assert.All(
            malformed,
            document => Assert.Throws<UnsupportedQueryExpressionException>(
                () => document.RequiredSnapshotDepth));
    }

    [Fact]
    public void Compilation_preserves_the_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
            () => QueryInterpreter.Compile<Row>(Document(True), cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    private static QueryDocument Document(QueryNode predicate) =>
        new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            QueryTarget.Session,
            predicate);

    private static QueryNode NestedNot(QueryNode operand, int levels)
    {
        QueryNode result = operand;
        for (int level = 0; level < levels; level++)
        {
            result = new NotNode(result);
        }

        return result;
    }

    private static void AssertRejected(QueryNode predicate, string messageFragment)
    {
        QueryDocument document = Document(predicate);
        UnsupportedQueryExpressionException compilationFailure =
            Assert.Throws<UnsupportedQueryExpressionException>(
                () => document.Compile<Row>());
        UnsupportedQueryExpressionException depthFailure =
            Assert.Throws<UnsupportedQueryExpressionException>(
                () => document.RequiredSnapshotDepth);

        Assert.Contains(
            messageFragment,
            compilationFailure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            messageFragment,
            depthFailure.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
