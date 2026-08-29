using System.Text.Json;
using System.Text.RegularExpressions;
using LibTmux.Query;
using LibTmux.Query.Json;

namespace LibTmux.UnitTests.Query;

public sealed class QueryJsonTests
{
    private sealed record Row(string SessionName, bool SessionAttached);

    private static readonly FieldNode SessionName =
        new(QueryTarget.Session, "session_name");

    private static readonly ConstantNode True =
        new(new BooleanConstant(true));

    public static TheoryData<string, QueryDocument> Goldens =>
        new()
        {
            {
                "string-and-comparison",
                QueryExtensions.Translate<Row>(
                    row => row.SessionName.StartsWith("dev") && row.SessionAttached)
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
            { "legacy-name-contains", QueryEdgeParser.ParseNameContains(QueryTarget.Window, "log") },
        };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Round_trips_every_version_one_golden_byte_for_byte(
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
    public void The_wire_matches_the_accepted_version_one_golden()
    {
        const string expected =
            """
            {"schema":"libtmux-query","version":1,"target":"session","predicate":{"kind":"comparison","operator":"containsOrdinal","left":{"kind":"field","target":"session","wireName":"session_name"},"right":{"kind":"constant","value":{"kind":"string","value":"dev"}}}}
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
            {"schema":"libtmux-query","version":1,"target":"session","predicate":{"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},"dialect":"dotnet","pattern":"^prod-[0-9]+$","semanticOptions":512}}
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
    public void Limits_may_tighten_the_frozen_ceilings_but_never_widen_them()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        string json = QueryJson.Serialize(document);

        Assert.NotNull(QueryJson.Deserialize(json, QueryJsonLimits.V1 with { MaximumNodes = 8 }));
        // Widening would let this reader accept a document another v1 reader
        // must reject, which is exactly what a frozen schema forbids.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.V1 with { MaximumNodes = 4096 }));
    }

    [Fact]
    public void An_oversized_or_too_deep_document_is_refused()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        string json = QueryJson.Serialize(document);

        Assert.Throws<JsonException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.V1 with { MaximumUtf8Bytes = 4 }));
        Assert.Throws<JsonException>(
            () => QueryJson.Deserialize(json, QueryJsonLimits.V1 with { MaximumNodes = 1 }));
    }

    [Fact]
    public void A_document_at_the_maximum_logical_depth_round_trips()
    {
        QueryNode predicate = True;
        for (int depth = 1; depth < QueryJsonLimits.V1.MaximumDepth; depth++)
        {
            predicate = new NotNode(predicate);
        }

        QueryDocument document = Document(predicate);

        Assert.Equal(document, QueryJson.Deserialize(QueryJson.Serialize(document)));
        Assert.Throws<JsonException>(
            () => QueryJson.Serialize(Document(new NotNode(predicate))));
    }

    [Fact]
    public void An_unknown_node_kind_is_refused_rather_than_guessed()
    {
        const string json =
            """{"schema":"libtmux-query","version":1,"target":"session","predicate":{"kind":"telepathy"}}""";

        Assert.Throws<JsonException>(() => QueryJson.Deserialize(json));
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
    public void Serialization_refuses_values_with_no_version_one_wire_form(
        string name,
        QueryDocument document)
    {
        Assert.NotEmpty(name);

        Assert.Throws<JsonException>(() => QueryJson.Serialize(document));
    }

    [Theory]
    [InlineData("someone.else", QueryDocument.CurrentVersion)]
    [InlineData(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion + 1)]
    public void Serialization_refuses_a_document_from_another_contract(
        string schema,
        int version)
    {
        QueryDocument document = new(schema, version, QueryTarget.Session, True);

        Assert.Throws<JsonException>(() => QueryJson.Serialize(document));
    }

    [Fact]
    public void Serialization_enforces_the_version_one_encoded_size_limit()
    {
        string value = new('a', QueryJsonLimits.V1.MaximumStringLength);
        QueryNode[] operands =
        [
            .. Enumerable.Range(0, 64).Select(
                _ => new StringNode(
                    QueryStringOperation.ContainsOrdinal,
                    SessionName,
                    new ConstantNode(new StringConstant(value)))),
        ];
        QueryDocument document = Document(new OrNode(operands));

        Assert.Throws<JsonException>(() => QueryJson.Serialize(document));
    }

    private static QueryDocument Document(
        QueryNode predicate,
        QueryTarget target = QueryTarget.Session) =>
        new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            target,
            predicate);
}
