using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class QuerySemanticsTests
{
    private sealed record Row(string SessionName, bool SessionAttached);

    private sealed record SessionCountRow(string SessionName, long SessionWindows);

    private sealed record WindowCountRow(string WindowName, long WindowPanes);

    [Fact]
    public void An_entity_translates_through_the_name_tmux_uses_for_the_field()
    {
        // A property name and its tmux field name are not derivable from one
        // another, so the catalog stores the pair explicitly.
        Assert.Equal(
            "session_name",
            Field(QueryExtensions.Translate<Session>(
                session => session.Name.StartsWith("build", StringComparison.Ordinal))));
        Assert.Equal(
            "session_attached",
            Field(QueryExtensions.Translate<Session>(session => session.Attached)));
        Assert.Equal(
            "window_name",
            Field(QueryExtensions.Translate<Window>(window => window.Name == "build")));

        // The one that a naming rule would never produce.
        Assert.Equal(
            "client_control",
            Field(QueryExtensions.Translate<Client>(client => client.IsControlClient)));
    }

    [Fact]
    public void An_entity_relation_translates_with_the_element_it_quantifies()
    {
        QueryDocument document = QueryExtensions.Translate<Session>(
            session => session.Windows.Any(window => window.Name == "build"));

        QuantifierNode quantifier = Assert.IsType<QuantifierNode>(document.Predicate);
        Assert.Equal("session_windows", quantifier.Relation.WireName);
        Assert.Equal(QueryTarget.Session, document.Target);
    }

    [Fact]
    public void A_row_a_caller_declares_still_names_the_wire_fields_itself()
    {
        // A type the catalog does not own is a projection, whose properties are
        // the wire names already. Both spellings reach the same document.
        Assert.Equal(
            "session_name",
            Field(QueryExtensions.Translate<Row>(
                row => row.SessionName.StartsWith("dev", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_property_outside_the_catalog_still_refuses_to_translate()
    {
        UnsupportedQueryExpressionException error =
            Assert.Throws<UnsupportedQueryExpressionException>(
                () => QueryExtensions.Translate<Pane>(pane => pane.Title == "x"));

        Assert.Contains("title", error.Message, StringComparison.Ordinal);
    }

    private static string Field(QueryDocument document) => document.Predicate switch
    {
        StringNode text => ((FieldNode)text.Left).WireName,
        ComparisonNode comparison => ((FieldNode)comparison.Left).WireName,
        FieldNode field => field.WireName,
        _ => throw new InvalidOperationException($"No field in {document.Predicate}."),
    };

    [Fact]
    public void Matching_translates_and_interprets_the_canonical_AST()
    {
        QueryDocument document = QueryExtensions.Translate<Row>(
            row => row.SessionName.StartsWith("dev", StringComparison.Ordinal)
                && row.SessionAttached);

        Assert.Equal(QueryDocument.CurrentSchema, document.Schema);
        Assert.Equal(QueryDocument.CurrentVersion, document.Version);
        Assert.Equal(QueryTarget.Session, document.Target);

        AndNode conjunction = Assert.IsType<AndNode>(document.Predicate);
        Assert.Equal(2, conjunction.Operands.Count);
        StringNode prefix = Assert.IsType<StringNode>(conjunction.Operands[0]);
        Assert.Equal(QueryStringOperation.StartsWithOrdinal, prefix.Operator);
        Assert.Equal(
            new FieldNode(QueryTarget.Session, "session_name"),
            Assert.IsType<FieldNode>(prefix.Left));
        Assert.Equal(
            new ConstantNode(new StringConstant("dev")),
            Assert.IsType<ConstantNode>(prefix.Right));
        ComparisonNode attached = Assert.IsType<ComparisonNode>(conjunction.Operands[1]);
        Assert.Equal(QueryComparison.Equal, attached.Operator);
        Assert.Equal(
            new ConstantNode(new BooleanConstant(true)),
            Assert.IsType<ConstantNode>(attached.Right));

        // The same predicate must mean the same thing in memory as on the wire.
        Func<Row, bool> compiled = document.Compile<Row>();
        Assert.True(compiled(new Row("devbox", true)));
        Assert.False(compiled(new Row("devbox", false)));
        Assert.False(compiled(new Row("prod", true)));

        IReadOnlyList<Row> matched = new[]
        {
            new Row("devbox", true),
            new Row("prod", true),
        }.Matching<Row>(
            row => row.SessionName.StartsWith("dev", StringComparison.Ordinal)
                && row.SessionAttached);
        Assert.Single(matched);
        Assert.Equal("devbox", matched[0].SessionName);
    }

    [Fact]
    public void Translation_refuses_a_field_outside_the_closed_catalog()
    {
        // A field the catalog does not carry cannot be put on the wire, so
        // translating it would build a document tmux could never answer.
        UnsupportedQueryExpressionException error =
            Assert.Throws<UnsupportedQueryExpressionException>(
                () => QueryExtensions.Translate<Unknown>(row => row.PaneTitle == "x"));
        Assert.Contains("pane_title", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relation_fields_keep_their_scalar_tmux_value_in_row_projections()
    {
        QueryDocument sessions = QueryExtensions.Translate<SessionCountRow>(
            row => row.SessionWindows > 1);
        QueryDocument panes = QueryExtensions.Translate<WindowCountRow>(
            row => row.WindowPanes == 2);

        Assert.True(sessions.Compile<SessionCountRow>()(new SessionCountRow("dev", 2)));
        Assert.True(panes.Compile<WindowCountRow>()(new WindowCountRow("main", 2)));
    }

    [Fact]
    public void A_boolean_field_is_a_complete_predicate()
    {
        QueryDocument document = new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            QueryTarget.Session,
            new FieldNode(QueryTarget.Session, "session_attached"));
        Func<Row, bool> predicate = document.Compile<Row>();

        Assert.True(predicate(new Row("build", true)));
        Assert.False(predicate(new Row("build", false)));
    }

    [Fact]
    public void Reflection_based_evaluation_declares_its_trimming_contract()
    {
        MethodInfo[] evaluationMethods =
        [
            .. typeof(QueryExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name is "Compile" or "Matching"),
        ];

        Assert.Equal(3, evaluationMethods.Length);
        Assert.All(
            evaluationMethods,
            method => Assert.NotNull(
                method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()));
    }

    [Fact]
    public void Translation_refuses_an_unsupported_node_rather_than_evaluating_it()
    {
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryExtensions.Translate<Row>(row => row.SessionName.Trim() == "X"));
    }

    [Fact]
    public void String_translation_preserves_the_selected_comparison_semantics()
    {
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryExtensions.Translate<Row>(row => row.SessionName.StartsWith("dev")));
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryExtensions.Translate<Row>(row => row.SessionName.EndsWith("box")));

        QueryDocument contains = QueryExtensions.Translate<Row>(
            row => row.SessionName.Contains("dev"));

        Assert.Equal(
            QueryStringOperation.ContainsOrdinal,
            Assert.IsType<StringNode>(contains.Predicate).Operator);
    }

    [Fact]
    public void Regex_translation_requires_explicit_culture_invariance()
    {
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryExtensions.Translate<Row>(
                row => Regex.IsMatch(row.SessionName, "^build", RegexOptions.IgnoreCase)));
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryExtensions.Translate<Row>(
                row => Regex.IsMatch(
                    row.SessionName,
                    "^build",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1))));

        QueryDocument document = QueryExtensions.Translate<Row>(
            row => Regex.IsMatch(
                row.SessionName,
                "^build",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        RegexNode regex = Assert.IsType<RegexNode>(document.Predicate);

        Assert.Equal(
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            regex.SemanticOptions);
    }

    [Fact]
    public void The_legacy_name_lookup_is_ordinal_and_target_scoped()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");

        Assert.Equal(QueryTarget.Session, document.Target);
        StringNode contains = Assert.IsType<StringNode>(document.Predicate);
        Assert.Equal(QueryStringOperation.ContainsOrdinal, contains.Operator);
        Assert.Equal(
            new FieldNode(QueryTarget.Session, "session_name"),
            Assert.IsType<FieldNode>(contains.Left));

        // tmux gives panes a command and a title, never a name.
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryEdgeParser.ParseNameContains(QueryTarget.Pane, "dev"));
    }

    [Fact]
    public void Quantifiers_fold_an_empty_relation_the_way_LINQ_does()
    {
        QueryDocument any = QueryExtensions.Translate<Parent>(
            parent => parent.SessionWindows.Any(child => child.WindowName == "x"));
        QueryDocument all = QueryExtensions.Translate<Parent>(
            parent => parent.SessionWindows.All(child => child.WindowName == "x"));
        var empty = new Parent([]);

        Assert.False(any.Compile<Parent>()(empty));
        Assert.True(all.Compile<Parent>()(empty));
        Assert.Equal(SnapshotDepth.Windows, any.RequiredSnapshotDepth);
    }

    private sealed record Unknown(string PaneTitle);

    private sealed record Child(string WindowName);

    private sealed record Parent(IReadOnlyList<Child> SessionWindows);

    [Fact]
    public void And_and_or_nodes_use_ordered_structural_equality_and_hashing()
    {
        QueryNode first = new FieldNode(QueryTarget.Session, "session_id");
        QueryNode second = new ConstantNode(new Int64Constant(3));

        var left = new AndNode([first, second]);
        var right = new AndNode([first, second]);
        var reordered = new AndNode([second, first]);

        // Structural, because a record would otherwise compare list references.
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        // Ordered, because the wire form and any pushdown preserve order.
        Assert.NotEqual(left, reordered);
        Assert.NotEqual<QueryNode>(left, new OrNode([first, second]));
    }
}
