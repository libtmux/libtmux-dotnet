using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LibTmux.Query.Json;

/// <summary>Bounds one JSON document so parsing cannot be weaponised.</summary>
/// <remarks>
/// Every dimension a hostile producer could grow is capped. Callers may tighten
/// a limit but never widen one.
/// </remarks>
/// <param name="MaximumDepth">Deepest nesting accepted.</param>
/// <param name="MaximumNodes">Most predicate nodes accepted.</param>
/// <param name="MaximumStringLength">Longest string value accepted.</param>
/// <param name="MaximumPatternLength">Longest regex pattern accepted.</param>
/// <param name="MaximumUtf8Bytes">Largest encoded document accepted.</param>
public sealed record QueryJsonLimits(
    int MaximumDepth,
    int MaximumNodes,
    int MaximumStringLength,
    int MaximumPatternLength,
    int MaximumUtf8Bytes)
{
    /// <summary>Gets the maximum supported document limits.</summary>
    public static QueryJsonLimits Default { get; } = new(
        MaximumDepth: QueryDocumentStructuralGuard.MaximumDepth,
        MaximumNodes: QueryDocumentStructuralGuard.MaximumNodeOccurrences,
        MaximumStringLength: 4096,
        MaximumPatternLength: QueryRegexSemantics.MaximumPatternLength,
        MaximumUtf8Bytes: 262144);

    internal QueryJsonLimits Clamp()
    {
        // Tightening is a caller's business; widening would let one reader
        // accept a document another reader must reject.
        if (MaximumDepth > Default.MaximumDepth
            || MaximumNodes > Default.MaximumNodes
            || MaximumStringLength > Default.MaximumStringLength
            || MaximumPatternLength > Default.MaximumPatternLength
            || MaximumUtf8Bytes > Default.MaximumUtf8Bytes
            || MaximumDepth < 0
            || MaximumNodes < 0
            || MaximumStringLength < 0
            || MaximumPatternLength < 0
            || MaximumUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueryJsonLimits),
                "Query JSON limits may tighten the default ceilings but never widen them.");
        }

        return this;
    }
}

/// <summary>Serializes query documents in their declared wire schema version.</summary>
/// <remarks>
/// JSON lives in its own package so the core library carries no serializer
/// dependency. A caller who never puts a query on a wire never pays for one.
/// </remarks>
public static class QueryJson
{
    /// <summary>Writes one document as versioned JSON.</summary>
    /// <param name="document">The document to write.</param>
    /// <returns>The encoded document, with no trailing newline.</returns>
    /// <exception cref="UnsupportedQueryExpressionException">
    /// The document has an unsupported wire form or encodes past the size limit.
    /// </exception>
    public static string Serialize(QueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            new QueryDocumentJsonConverter(QueryJsonLimits.Default)
                .Write(writer, document, new JsonSerializerOptions());
        }

        byte[] encoded = buffer.ToArray();
        if (encoded.Length > QueryJsonLimits.Default.MaximumUtf8Bytes)
        {
            throw new UnsupportedQueryExpressionException("Query document exceeds the maximum encoded size.");
        }

        return Encoding.UTF8.GetString(encoded);
    }

    /// <summary>Reads one versioned JSON document.</summary>
    /// <param name="json">The encoded document.</param>
    /// <param name="limits">Limits to apply, never wider than the defaults.</param>
    /// <returns>The decoded document.</returns>
    /// <exception cref="System.Text.Json.JsonException">The text is not JSON.</exception>
    /// <exception cref="UnsupportedQueryExpressionException">
    /// The text is JSON, but not a supported wire form: an unknown schema, version,
    /// target, node or constant, a duplicate or unknown member, or a value past
    /// one of the document limits.
    /// </exception>
    public static QueryDocument Deserialize(string json, QueryJsonLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        QueryJsonLimits bounds = (limits ?? QueryJsonLimits.Default).Clamp();
        if (Encoding.UTF8.GetByteCount(json) > bounds.MaximumUtf8Bytes)
        {
            throw new UnsupportedQueryExpressionException("Query document exceeds the maximum encoded size.");
        }

        using JsonDocument parsed = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = ParserDepth(bounds.MaximumDepth) });
        try
        {
            JsonElement root = parsed.RootElement;
            QueryJsonWireRules.ValidateEnvelope(root);

            // Reject unsupported envelopes before interpreting their predicates.
            string schema = root.GetProperty("schema").GetString()
                ?? throw new UnsupportedQueryExpressionException("Query document names no schema.");
            if (!string.Equals(schema, QueryDocument.CurrentSchema, StringComparison.Ordinal))
            {
                throw new UnsupportedQueryExpressionException(
                    $"Query document names schema '{schema}', which this reader does not know.");
            }

            int version = root.GetProperty("version").GetInt32();
            if (version != QueryDocument.CurrentVersion)
            {
                throw new UnsupportedQueryExpressionException(
                    $"Query document is version {version}; this reader understands "
                    + $"{QueryDocument.CurrentVersion}.");
            }

            var reader = new QueryDocumentJsonReader(bounds);
            QueryDocument document = new(
                schema,
                version,
                QueryDocumentJsonReader.ReadTarget(root.GetProperty("target")),
                reader.ReadNode(root.GetProperty("predicate"), depth: 1));
            QueryDocumentValidator.Validate(document);
            return document;
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException
            or InvalidCastException
            or InvalidOperationException
            or FormatException)
        {
            // A document shaped wrongly enough to fault element access is the
            // same refusal the reader makes deliberately, so it reads the same.
            throw new UnsupportedQueryExpressionException(
                "Query document does not match its declared wire form.",
                string.Empty,
                exception);
        }
    }

    private static int ParserDepth(int queryDepth) => (queryDepth * 2) + 4;
}
