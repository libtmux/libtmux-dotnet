using System.Text.Json;
using System.Text.RegularExpressions;
using LibTmux.Query;
using LibTmux.Query.Json;

namespace LibTmux.UnitTests.Query;

public sealed class QueryJsonTests
{
    private sealed record Row(string SessionName, bool SessionAttached);

    private sealed record SessionCountRow(string SessionName, long SessionWindows);

    private sealed record PanePathRow(string PaneCurrentPath);

    private sealed record PaneParentRow(WindowParentRow PaneWindow);

    private sealed record WindowParentRow(string WindowName);

    private static readonly FieldNode SessionName =
        new(QueryTarget.Session, "session_name");

    private static readonly ConstantNode True =
        new(new BooleanConstant(true));

    [Fact]
    public void Obsolete_versions_are_rejected_before_reading_or_evaluating_a_predicate()
    {
        const string obsolete =
            """
            {"schema":"libtmux-query","version":1,"target":"session","predicate":{"kind":"constant","value":{"kind":"boolean","value":true}}}
            """;
        QueryDocument document = new(QueryDocument.CurrentSchema, 1, QueryTarget.Session, True);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(obsolete));
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Serialize(document));
        Assert.Throws<UnsupportedQueryExpressionException>(() => document.RequiredSnapshotDepth);
    }

    [Fact]
    public void Current_path_round_trips_and_evaluates_locally()
    {
        const string json =
            """
            {"schema":"libtmux-query","version":2,"target":"pane","predicate":{"kind":"comparison","operator":"startsWithOrdinal","left":{"kind":"field","target":"pane","wireName":"pane_current_path"},"right":{"kind":"constant","value":{"kind":"string","value":"/srv/"}}}}
            """;
        QueryDocument document = QueryJson.Deserialize(json);

        Assert.Equal(2, document.Version);
        Assert.Equal(SnapshotDepth.Panes, document.RequiredSnapshotDepth);
        Assert.Equal(json, QueryJson.Serialize(document));
        Assert.Equal(
            [new PanePathRow("/srv/api")],
            new[] { new PanePathRow("/srv/api"), new PanePathRow("/tmp/api") }
                .Matching<PanePathRow>(document));
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(
            json.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal)));
    }

    [Fact]
    public void Related_nodes_round_trip_and_evaluate_locally()
    {
        const string json =
            """
            {"schema":"libtmux-query","version":2,"target":"pane","predicate":{"kind":"related","relation":{"kind":"field","target":"pane","wireName":"pane_window"},"predicate":{"kind":"comparison","operator":"stringEqualOrdinal","left":{"kind":"field","target":"window","wireName":"window_name"},"right":{"kind":"constant","value":{"kind":"string","value":"editor"}}}}}
            """;
        QueryDocument document = QueryJson.Deserialize(json);
        Func<PaneParentRow, bool> predicate = document.Compile<PaneParentRow>();

        Assert.Equal(json, QueryJson.Serialize(document));
        Assert.Equal(SnapshotDepth.Panes, document.RequiredSnapshotDepth);
        Assert.True(predicate(new PaneParentRow(new WindowParentRow("editor"))));
        Assert.False(predicate(new PaneParentRow(new WindowParentRow("build"))));
        Assert.Throws<UnsupportedQueryExpressionException>(() => predicate(new PaneParentRow(null!)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(
            json.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(
            json.Replace("\"kind\":\"related\"", "\"kind\":\"related\",\"extra\":true", StringComparison.Ordinal)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(
            json.Replace("\"target\":\"window\",\"wireName\":\"window_name\"", "\"target\":\"session\",\"wireName\":\"session_name\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Relation_nodes_validate_cardinality_before_binding_a_projection()
    {
        QueryNode truth = new ConstantNode(new BooleanConstant(true));
        QueryDocument[] invalid =
        [
            new(QueryDocument.CurrentSchema, 2, QueryTarget.Session,
                new RelatedNode(new FieldNode(QueryTarget.Session, "session_windows"), truth)),
            new(QueryDocument.CurrentSchema, 2, QueryTarget.Pane,
                new QuantifierNode(QueryQuantifier.Any, new FieldNode(QueryTarget.Pane, "pane_window"), truth)),
        ];

        Assert.All(invalid, document =>
        {
            Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Serialize(document));
            Assert.Throws<UnsupportedQueryExpressionException>(() => document.RequiredSnapshotDepth);
        });
    }

    public static TheoryData<string, QueryDocument> TranslatedDocuments =>
        new()
        {
            {
                "string-and-comparison",
                QueryExtensions.Translate<Row>(
                    row => row.SessionName.StartsWith("dev", StringComparison.Ordinal)
                        && row.SessionAttached)
            },
            {
                "negated-contains",
                QueryExtensions.Translate<Row>(row => !row.SessionName.Contains("prod"))
            },
            {
                "disjunction",
                QueryExtensions.Translate<Row>(
                    row => row.SessionName == "a" || row.SessionAttached)
            },
            {
                "numeric-comparison",
                QueryExtensions.Translate<SessionCountRow>(row => row.SessionWindows > 1)
            },
            { "legacy-name-contains", QueryEdgeParser.ParseNameContains(QueryTarget.Window, "log") },
        };

    [Theory]
    [MemberData(nameof(TranslatedDocuments))]
    public void Translated_documents_round_trip_byte_for_byte(
        string name,
        QueryDocument document)
    {
        Assert.NotEmpty(name);

        string json = QueryJson.Serialize(document);
        QueryDocument restored = QueryJson.Deserialize(json);

        // Byte-for-byte, not merely equivalent: the wire form is the stable
        // artifact, so a reserialized document must be indistinguishable.
        Assert.Equal(json, QueryJson.Serialize(restored));
        Assert.Equal(document, restored);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_matches_the_current_golden()
    {
        const string expected =
            """
            {"schema":"libtmux-query","version":2,"target":"session","predicate":{"kind":"comparison","operator":"containsOrdinal","left":{"kind":"field","target":"session","wireName":"session_name"},"right":{"kind":"constant","value":{"kind":"string","value":"dev"}}}}
            """;
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");

        Assert.Equal(expected, QueryJson.Serialize(document));
        Assert.Equal(document, QueryJson.Deserialize(expected));
    }

    [Fact]
    public void The_wire_matches_the_retained_regex_golden()
    {
        const string expected =
            """
            {"schema":"libtmux-query","version":2,"target":"session","predicate":{"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},"dialect":"dotnet","pattern":"^prod-[0-9]+$","semanticOptions":512}}
            """;
        QueryDocument document = Document(new RegexNode(
            SessionName,
            "dotnet",
            "^prod-[0-9]+$",
            RegexOptions.CultureInvariant));

        Assert.Equal(expected, QueryJson.Serialize(document));
        Assert.Equal(document, QueryJson.Deserialize(expected));
    }

    [Fact]
    public void The_schema_field_manifest_matches_the_runtime_catalog()
    {
        const string resourceName = "LibTmux.UnitTests.QuerySchema.json";
        using Stream stream = typeof(QueryJsonTests).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing embedded query schema.");
        using JsonDocument schema = JsonDocument.Parse(stream);
        JsonElement definitions = schema.RootElement.GetProperty("$defs");
        string[] fields = [.. QueryFieldCatalog.WireNames];

        Assert.Equal(
            fields.Order(StringComparer.Ordinal),
            DirectEnumValues(definitions.GetProperty("field"), "wireName"));
        AssertKind(definitions, "booleanField", QueryValueKind.Boolean, fields);
        AssertKind(definitions, "stringField", QueryValueKind.String, fields);
        AssertKind(definitions, "int64Field", QueryValueKind.Int64, fields);
        Assert.Equal(
            fields.Where(
                    name => QueryFieldCatalog.TryGetKind(name, out QueryValueKind actual)
                        && actual == QueryValueKind.TypedId)
                .Order(StringComparer.Ordinal),
            ConstFieldValues(
                definitions,
                "sessionIdField",
                "windowIdField",
                "paneIdField",
                "clientIdField"));
        Assert.Equal(
            fields.Where(QueryFieldCatalog.IsRelation)
                .Order(StringComparer.Ordinal),
            ConstrainedEnumValues(definitions.GetProperty("relationField"), "wireName"));

        JsonElement targetCases = definitions.GetProperty("field")
            .GetProperty("allOf")[0]
            .GetProperty("oneOf");
        foreach (JsonElement targetCase in targetCases.EnumerateArray())
        {
            JsonElement properties = targetCase.GetProperty("properties");
            QueryTarget target = Enum.Parse<QueryTarget>(
                properties.GetProperty("target").GetProperty("const").GetString()!,
                ignoreCase: true);
            Assert.Equal(
                fields.Where(
                        name => QueryFieldCatalog.TryGetTarget(name, out QueryTarget actual)
                            && actual == target)
                    .Order(StringComparer.Ordinal),
                DirectEnumValues(targetCase, "wireName"));
        }
    }

    [Fact]
    public void Limits_may_tighten_the_frozen_ceilings_but_never_widen_them()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        string json = QueryJson.Serialize(document);

        Assert.NotNull(QueryJson.Deserialize(json, QueryJsonLimits.Default with { MaximumNodes = 8 }));
        // Widening would let this reader accept a document another reader
        // must reject, which is exactly what a frozen schema forbids.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.Default with { MaximumNodes = 4096 }));
    }

    [Fact]
    public void An_oversized_or_too_deep_document_is_refused()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        string json = QueryJson.Serialize(document);

        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.Default with { MaximumUtf8Bytes = 4 }));
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.Default with { MaximumNodes = 1 }));
    }

    [Fact]
    public void A_document_at_the_maximum_logical_depth_round_trips()
    {
        QueryNode predicate = True;
        for (int depth = 1; depth < QueryJsonLimits.Default.MaximumDepth; depth++)
        {
            predicate = new NotNode(predicate);
        }

        QueryDocument document = Document(predicate);

        Assert.Equal(document, QueryJson.Deserialize(QueryJson.Serialize(document)));
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Serialize(Document(new NotNode(predicate))));
    }

    [Fact]
    public void Serialization_applies_structural_budgets_before_semantic_validation()
    {
        QueryNode tooDeep = SessionName;
        for (int depth = 0; depth < QueryJsonLimits.Default.MaximumDepth; depth++)
        {
            tooDeep = new NotNode(tooDeep);
        }

        UnsupportedQueryExpressionException depthFailure = Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Serialize(Document(tooDeep)));
        Assert.Contains("maximum nesting depth", depthFailure.Message, StringComparison.Ordinal);

        QueryNode[] tooMany =
        [
            .. Enumerable.Repeat<QueryNode>(
                SessionName,
                QueryJsonLimits.Default.MaximumNodes),
        ];
        UnsupportedQueryExpressionException nodeFailure = Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Serialize(Document(new OrNode(tooMany))));
        Assert.Contains("maximum node count", nodeFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_node_kind_is_refused_rather_than_guessed()
    {
        const string json =
            """{"schema":"libtmux-query","version":2,"target":"session","predicate":{"kind":"telepathy"}}""";

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void Supplementary_unicode_round_trips_as_one_string_value()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "build-\U0001F680");

        string json = QueryJson.Serialize(document);

        Assert.Equal(document, QueryJson.Deserialize(json));
    }

    public static TheoryData<string, QueryDocument> InvalidWriterDocuments =>
        new()
        {
            {
                "target",
                Document(True, target: (QueryTarget)99)
            },
            {
                "comparison",
                Document(new ComparisonNode((QueryComparison)99, True, True))
            },
            {
                "string operation",
                Document(new StringNode((QueryStringOperation)99, SessionName, True))
            },
            {
                "quantifier",
                Document(new QuantifierNode(
                    (QueryQuantifier)99,
                    new FieldNode(QueryTarget.Session, "session_windows"),
                    True))
            },
            {
                "regex options",
                Document(new RegexNode(
                    SessionName,
                    "dotnet",
                    "^build",
                    RegexOptions.NonBacktracking))
            },
            {
                "regex dialect",
                Document(new RegexNode(SessionName, "pcre", "^build", RegexOptions.None))
            },
        };

    [Theory]
    [MemberData(nameof(InvalidWriterDocuments))]
    public void Serialization_refuses_values_with_no_wire_form(
        string name,
        QueryDocument document)
    {
        Assert.NotEmpty(name);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Serialize(document));
    }

    [Theory]
    [InlineData("someone.else", QueryDocument.CurrentVersion)]
    [InlineData(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion + 1)]
    public void Serialization_refuses_a_document_from_another_contract(
        string schema,
        int version)
    {
        QueryDocument document = new(schema, version, QueryTarget.Session, True);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Serialize(document));
    }

    [Fact]
    public void Serialization_enforces_the_encoded_size_limit()
    {
        string value = new('a', QueryJsonLimits.Default.MaximumStringLength);
        QueryNode[] operands =
        [
            .. Enumerable.Range(0, 64).Select(
                _ => new StringNode(
                    QueryStringOperation.ContainsOrdinal,
                    SessionName,
                    new ConstantNode(new StringConstant(value)))),
        ];
        QueryDocument document = Document(new OrNode(operands));

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Serialize(document));
    }

    private static QueryDocument Document(
        QueryNode predicate,
        QueryTarget target = QueryTarget.Session) =>
        new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            target,
            predicate);

    private static void AssertKind(
        JsonElement definitions,
        string definition,
        QueryValueKind kind,
        IReadOnlyList<string> fields) =>
        Assert.Equal(
            fields.Where(
                    name => QueryFieldCatalog.TryGetKind(name, out QueryValueKind actual)
                        && actual == kind)
                .Order(StringComparer.Ordinal),
            ConstrainedEnumValues(definitions.GetProperty(definition), "wireName"));

    private static string[] ConstrainedEnumValues(
        JsonElement definition,
        string property) =>
        DirectEnumValues(definition.GetProperty("allOf")[1], property);

    private static string[] ConstFieldValues(
        JsonElement definitions,
        params string[] definitionNames) =>
    [
        .. definitionNames.Select(
                name => definitions.GetProperty(name)
                    .GetProperty("allOf")[1]
                    .GetProperty("properties")
                    .GetProperty("wireName")
                    .GetProperty("const")
                    .GetString()!)
            .Order(StringComparer.Ordinal),
    ];

    private static string[] DirectEnumValues(JsonElement definition, string property) =>
    [
        .. definition.GetProperty("properties")
            .GetProperty(property)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .Order(StringComparer.Ordinal),
    ];
}
