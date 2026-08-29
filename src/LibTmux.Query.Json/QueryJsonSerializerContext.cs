using System.Text;
using System.Text.Json;

namespace LibTmux.Query.Json;

/// <summary>Bounds one JSON document so parsing cannot be weaponised.</summary>
/// <remarks>
/// Every dimension a hostile producer could grow is capped, and the v1
/// ceilings are frozen: callers may tighten a limit but never widen one.
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
    /// <summary>The frozen v1 ceilings.</summary>
    public static QueryJsonLimits V1 { get; } = new(
        MaximumDepth: 32,
        MaximumNodes: 512,
        MaximumStringLength: 4096,
        MaximumPatternLength: 1024,
        MaximumUtf8Bytes: 262144);

    internal QueryJsonLimits Clamp()
    {
        // Tightening is a caller's business; widening would let one reader
        // accept a document another v1 reader must reject.
        if (MaximumDepth > V1.MaximumDepth
            || MaximumNodes > V1.MaximumNodes
            || MaximumStringLength > V1.MaximumStringLength
            || MaximumPatternLength > V1.MaximumPatternLength
            || MaximumUtf8Bytes > V1.MaximumUtf8Bytes
            || MaximumDepth < 0
            || MaximumNodes < 0
            || MaximumStringLength < 0
            || MaximumPatternLength < 0
            || MaximumUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueryJsonLimits),
                "Query JSON limits may tighten the v1 ceilings but never widen them.");
        }

        return this;
    }
}

/// <summary>Serializes query documents to the stable v1 wire form.</summary>
/// <remarks>
/// JSON lives in its own package so the core library carries no serializer
/// dependency. A caller who never puts a query on a wire never pays for one.
/// </remarks>
public static class QueryJson
{
    /// <summary>Writes one document as v1 JSON.</summary>
    /// <param name="document">The document to write.</param>
    /// <returns>The encoded document, with no trailing newline.</returns>
    public static string Serialize(QueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            new QueryDocumentJsonConverter(QueryJsonLimits.V1)
                .Write(writer, document, new JsonSerializerOptions());
        }

        byte[] encoded = buffer.ToArray();
        if (encoded.Length > QueryJsonLimits.V1.MaximumUtf8Bytes)
        {
            throw new JsonException("Query document exceeds the maximum encoded size.");
        }

        return Encoding.UTF8.GetString(encoded);
    }

    /// <summary>Reads one v1 JSON document.</summary>
    /// <param name="json">The encoded document.</param>
    /// <param name="limits">Limits to apply, never wider than v1.</param>
    /// <returns>The decoded document.</returns>
    /// <exception cref="JsonException">The document is malformed or oversized.</exception>
    public static QueryDocument Deserialize(string json, QueryJsonLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        QueryJsonLimits bounds = (limits ?? QueryJsonLimits.V1).Clamp();
        if (Encoding.UTF8.GetByteCount(json) > bounds.MaximumUtf8Bytes)
        {
            throw new JsonException("Query document exceeds the maximum encoded size.");
        }

        using JsonDocument parsed = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = bounds.MaximumDepth });
        try
        {
            JsonElement root = parsed.RootElement;

            // Schema and version must be checked before anything else is read, or
            // a v2 payload gets silently parsed under v1 rules.
            string schema = root.GetProperty("schema").GetString()
                ?? throw new JsonException("Query document names no schema.");
            if (!string.Equals(schema, QueryDocument.CurrentSchema, StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Query document names schema '{schema}', which this reader does not know.");
            }

            int version = root.GetProperty("version").GetInt32();
            if (version != QueryDocument.CurrentVersion)
            {
                throw new JsonException(
                    $"Query document is version {version}; this reader understands "
                    + $"{QueryDocument.CurrentVersion}.");
            }

            var reader = new QueryDocumentJsonReader(bounds);
            return new QueryDocument(
                schema,
                version,
                QueryDocumentJsonReader.ReadTarget(root.GetProperty("target")),
                reader.ReadNode(root.GetProperty("predicate"), depth: 1));
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException
            or InvalidCastException
            or InvalidOperationException
            or FormatException)
        {
            throw new JsonException("Query document does not match the v1 wire form.", exception);
        }
    }
}
