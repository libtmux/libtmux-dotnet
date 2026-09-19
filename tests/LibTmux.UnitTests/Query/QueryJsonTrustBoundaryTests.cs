using System.Text.Json;
using LibTmux.Query;
using LibTmux.Query.Json;

namespace LibTmux.UnitTests.Query;

/// <summary>Proves a query document is treated as input rather than as instructions.</summary>
/// <remarks>
/// Deserialization is this library's trust boundary: declared limits must hold
/// while reading a document, and a document cannot nominate its own schema.
/// </remarks>
public sealed class QueryJsonTrustBoundaryTests
{
    private static string Document(
        string predicate,
        string schema = QueryDocument.CurrentSchema,
        int version = 1) =>
        $$"""
        {"schema":"{{schema}}","version":{{version}},"target":"session","predicate":{{predicate}}}
        """;

    private const string TrivialPredicate =
        """{"kind":"constant","value":{"kind":"boolean","value":true}}""";

    [Fact]
    public void A_document_naming_another_schema_is_refused()
    {
        // Reading a foreign schema with v1 rules is a silent misinterpretation,
        // which is worse than a failure.
        UnsupportedQueryExpressionException failure = Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Deserialize(Document(TrivialPredicate, schema: "someone.else")));

        Assert.Contains("someone.else", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_naming_a_future_version_is_refused()
    {
        UnsupportedQueryExpressionException failure = Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryJson.Deserialize(Document(TrivialPredicate, version: 2)));

        Assert.Contains("version 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_longer_than_the_limit_is_refused_on_the_way_in()
    {
        string oversized = new('a', QueryJsonLimits.V1.MaximumStringLength + 1);
        string json = Document(
            $$"""{"kind":"constant","value":{"kind":"string","value":"{{oversized}}" } }""");

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_pattern_longer_than_the_limit_is_refused_on_the_way_in()
    {
        string oversized = new('a', QueryJsonLimits.V1.MaximumPatternLength + 1);
        string json = Document(
            $$"""
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":"dotnet","pattern":"{{oversized}}","semanticOptions":0}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_regex_dialect_this_library_cannot_evaluate_is_refused()
    {
        string json = Document(
            """
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":"pcre","pattern":"^a","semanticOptions":0}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void Regex_options_outside_the_supported_set_are_refused()
    {
        // An unchecked integer becomes any bit pattern as a flags enum; 1024 is
        // RegexOptions.NonBacktracking, which this library's translation never emits.
        string json = Document(
            """
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":"dotnet","pattern":"^a","semanticOptions":1024}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void Regex_options_without_culture_invariance_are_refused()
    {
        string json = Document(
            """
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":"dotnet","pattern":"^a","semanticOptions":0}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_malformed_regex_is_refused_on_the_way_in()
    {
        string json = Document(
            """
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":"dotnet","pattern":"(","semanticOptions":512}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void An_unknown_quantifier_is_refused_rather_than_treated_as_all()
    {
        string json = Document(
            """
            {"kind":"quantifier","quantifier":"sometimes",
             "relation":{"kind":"field","target":"session","wireName":"session_windows"},
             "predicate":{"kind":"constant","value":{"kind":"boolean","value":true}}}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Theory]
    [InlineData("\"kind\":\"string\",\"value\":null")]
    [InlineData("\"kind\":\"typedId\",\"type\":\"session\",\"value\":null")]
    public void Null_constant_text_is_refused(string members)
    {
        string json = Document(
            $$"""{"kind":"constant","value":{ {{members}} } }""");

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_null_regex_dialect_is_refused()
    {
        string json = Document(
            """
            {"kind":"regex","input":{"kind":"field","target":"session","wireName":"session_name"},
             "dialect":null,"pattern":"^a","semanticOptions":0}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_predicate_with_no_kind_is_refused()
    {
        string json = Document("{}");

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_refused_document_reads_as_LibTmux_from_either_package()
    {
        // The whole point: one catch covers a document this library will not
        // accept, whichever package noticed it. Serializing used to answer a
        // System.Text.Json type for the same cause the core answered its own.
        Assert.IsAssignableFrom<LibTmuxException>(
            Record.Exception(() => QueryJson.Deserialize(Document("{}"))));
        Assert.IsAssignableFrom<LibTmuxException>(
            Record.Exception(() => QueryJson.Deserialize(
                Document(
                    "{\"kind\":\"constant\",\"value\":{\"kind\":\"boolean\",\"value\":true}}",
                    schema: "someone.else"))));
        Assert.IsAssignableFrom<LibTmuxException>(
            Record.Exception(() => QueryJson.Deserialize(
                Document("{\"kind\":\"nonsense\"}"))));
    }

    [Fact]
    public void Text_that_is_not_json_still_reports_a_json_error()
    {
        // The boundary the contract promises: JsonException means the text is
        // not JSON. Everything a v1 document may get wrong is a LibTmux
        // failure, so one catch covers a bad document from either package.
        // ThrowsAny: the parser answers JsonReaderException, which is one.
        Assert.ThrowsAny<JsonException>(() => QueryJson.Deserialize("{\"schema\":"));
        Assert.ThrowsAny<JsonException>(() => QueryJson.Deserialize("not json at all"));
    }

    [Theory]
    [InlineData(
        "{\"kind\":\"constant\",\"value\":{\"kind\":\"boolean\",\"value\":true},\"extra\":false}")]
    [InlineData(
        "{\"kind\":\"constant\",\"kind\":\"constant\",\"value\":{\"kind\":\"boolean\",\"value\":true}}")]
    [InlineData(
        "{\"kind\":\"constant\",\"value\":{\"kind\":\"boolean\",\"value\":true,\"extra\":false}}")]
    public void Unknown_or_duplicate_node_members_are_refused(string predicate) =>
        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(Document(predicate)));

    [Fact]
    public void An_unknown_envelope_member_is_refused()
    {
        const string json =
            """
            {"schema":"libtmux-query","version":1,"target":"session","predicate":{"kind":"constant","value":{"kind":"boolean","value":true}},"extra":false}
            """;

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    [Fact]
    public void A_field_outside_the_catalog_cannot_be_read_from_an_element()
    {
        // A FieldNode is forgeable, so the interpreter can't trust one came from
        // translation; resolving names by convention would expose any public property.
        QueryDocument forged = new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            QueryTarget.Session,
            new StringNode(
                QueryStringOperation.EqualsOrdinal,
                new FieldNode(QueryTarget.Session, "connection"),
                new ConstantNode(new StringConstant("anything"))));

        UnsupportedQueryExpressionException failure =
            Assert.Throws<UnsupportedQueryExpressionException>(
                () => forged.Compile<SessionRow>()(new SessionRow("build", true)));

        Assert.Contains("connection", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_outside_the_catalog_is_refused_while_deserializing()
    {
        string json = Document(
            """
            {"kind":"comparison","operator":"stringEqualOrdinal",
             "left":{"kind":"field","target":"session","wireName":"connection"},
             "right":{"kind":"constant","value":{"kind":"string","value":"anything"}}}
            """);

        Assert.Throws<UnsupportedQueryExpressionException>(() => QueryJson.Deserialize(json));
    }

    private sealed record SessionRow(string SessionName, bool SessionAttached);
}
