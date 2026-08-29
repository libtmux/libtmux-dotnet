using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LibTmux.Query.Json;

internal static class QueryJsonWireRules
{
    internal static int ScalarLength(string? value, string description)
    {
        if (value is null)
        {
            throw new JsonException($"{description} is null.");
        }

        int scalars = 0;
        for (int index = 0; index < value.Length; index++, scalars++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new JsonException($"{description} contains an unpaired surrogate.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new JsonException($"{description} contains an unpaired surrogate.");
            }
        }

        return scalars;
    }

    internal static void ValidateRegex(RegexNode regex, QueryJsonLimits limits)
    {
        if (!string.Equals(
                regex.Dialect,
                QueryRegexSemantics.Dialect,
                StringComparison.Ordinal))
        {
            throw new JsonException($"Regex dialect '{regex.Dialect}' is not supported.");
        }

        if (ScalarLength(regex.Pattern, "Regex pattern") > limits.MaximumPatternLength)
        {
            throw new JsonException("Regex pattern exceeds the maximum length.");
        }

        if (!QueryRegexSemantics.IsSupported(regex.SemanticOptions))
        {
            throw new JsonException("Regex names options this writer does not support.");
        }
    }
}

/// <summary>Reads and writes the stable v1 wire form of a query document.</summary>
/// <remarks>
/// The wire form is hand-written rather than reflection-derived so the schema
/// is decoupled from the CLR shape: renaming a record property must not change
/// the bytes a v1 reader expects.
/// </remarks>
internal sealed class QueryDocumentJsonConverter : JsonConverter<QueryDocument>
{
    private readonly QueryJsonLimits _limits;
    private int _nodes;

    internal QueryDocumentJsonConverter(QueryJsonLimits limits) => _limits = limits;

    public override QueryDocument Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("Query documents are read through QueryJson.");

    public override void Write(
        Utf8JsonWriter writer,
        QueryDocument value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.Schema, QueryDocument.CurrentSchema, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Query document names schema '{value.Schema}', which this writer does not know.");
        }

        if (value.Version != QueryDocument.CurrentVersion)
        {
            throw new JsonException(
                $"Query document is version {value.Version}; this writer understands "
                + $"{QueryDocument.CurrentVersion}.");
        }

        _nodes = 0;
        writer.WriteStartObject();
        writer.WriteString("schema", value.Schema);
        writer.WriteNumber("version", value.Version);
        writer.WriteString("target", Wire(value.Target));
        writer.WritePropertyName("predicate");
        WriteNode(writer, value.Predicate, depth: 1);
        writer.WriteEndObject();
    }

    private static string Wire(QueryTarget target) => target switch
    {
        QueryTarget.Session => "session",
        QueryTarget.Window => "window",
        QueryTarget.Pane => "pane",
        QueryTarget.Client => "client",
        _ => throw new JsonException("Query document names an unknown target."),
    };

    private static string Wire(QueryComparison comparison) => comparison switch
    {
        QueryComparison.Equal => "equal",
        QueryComparison.NotEqual => "notEqual",
        QueryComparison.LessThan => "lessThan",
        QueryComparison.LessThanOrEqual => "lessThanOrEqual",
        QueryComparison.GreaterThan => "greaterThan",
        QueryComparison.GreaterThanOrEqual => "greaterThanOrEqual",
        _ => throw new JsonException("Query document names an unknown comparison."),
    };

    private static string Wire(QueryStringOperation operation) => operation switch
    {
        QueryStringOperation.EqualsOrdinal => "stringEqualOrdinal",
        QueryStringOperation.EqualsOrdinalIgnoreCase => "stringEqualOrdinalIgnoreCase",
        QueryStringOperation.StartsWithOrdinal => "startsWithOrdinal",
        QueryStringOperation.EndsWithOrdinal => "endsWithOrdinal",
        QueryStringOperation.ContainsOrdinal => "containsOrdinal",
        _ => throw new JsonException("Query document names an unknown string operation."),
    };

    private static string Wire(QueryQuantifier quantifier) => quantifier switch
    {
        QueryQuantifier.Any => "any",
        QueryQuantifier.All => "all",
        _ => throw new JsonException("Query document names an unknown quantifier."),
    };

    private void WriteNode(Utf8JsonWriter writer, QueryNode node, int depth)
    {
        if (node is null)
        {
            throw new JsonException("Query document contains a null node.");
        }

        if (depth > _limits.MaximumDepth)
        {
            throw new JsonException("Query document exceeds the maximum nesting depth.");
        }

        if (++_nodes > _limits.MaximumNodes)
        {
            throw new JsonException("Query document exceeds the maximum node count.");
        }

        writer.WriteStartObject();
        switch (node)
        {
            case AndNode and:
                WriteOperands(writer, "and", and.Operands, depth);
                break;
            case OrNode or:
                WriteOperands(writer, "or", or.Operands, depth);
                break;
            case NotNode not:
                writer.WriteString("kind", "not");
                writer.WritePropertyName("operand");
                WriteNode(writer, not.Operand, depth + 1);
                break;
            case ComparisonNode comparison:
                writer.WriteString("kind", "comparison");
                writer.WriteString("operator", Wire(comparison.Operator));
                WritePair(writer, comparison.Left, comparison.Right, depth);
                break;
            case StringNode text:
                writer.WriteString("kind", "comparison");
                writer.WriteString("operator", Wire(text.Operator));
                WritePair(writer, text.Left, text.Right, depth);
                break;
            case RegexNode regex:
                WriteRegex(writer, regex, depth);
                break;
            case QuantifierNode quantifier:
                writer.WriteString("kind", "quantifier");
                writer.WriteString(
                    "quantifier",
                    Wire(quantifier.Quantifier));
                writer.WritePropertyName("relation");
                WriteNode(writer, quantifier.Relation, depth + 1);
                writer.WritePropertyName("predicate");
                WriteNode(writer, quantifier.Predicate, depth + 1);
                break;
            case FieldNode field:
                writer.WriteString("kind", "field");
                writer.WriteString("target", Wire(field.Target));
                WriteBoundedString(writer, "wireName", field.WireName, "Field wire name");
                break;
            case ConstantNode constant:
                writer.WriteString("kind", "constant");
                writer.WritePropertyName("value");
                WriteConstant(writer, constant.Value);
                break;
            default:
                throw new JsonException($"Node '{node.GetType().Name}' has no v1 wire form.");
        }

        writer.WriteEndObject();
    }

    private void WriteRegex(Utf8JsonWriter writer, RegexNode regex, int depth)
    {
        QueryJsonWireRules.ValidateRegex(regex, _limits);

        writer.WriteString("kind", "regex");
        writer.WritePropertyName("input");
        WriteNode(writer, regex.Input, depth + 1);
        writer.WriteString("dialect", regex.Dialect);
        writer.WriteString("pattern", regex.Pattern);
        writer.WriteNumber("semanticOptions", (int)regex.SemanticOptions);
    }

    private void WriteOperands(
        Utf8JsonWriter writer,
        string kind,
        IReadOnlyList<QueryNode> operands,
        int depth)
    {
        writer.WriteString("kind", kind);
        writer.WriteStartArray("operands");
        foreach (QueryNode operand in operands)
        {
            WriteNode(writer, operand, depth + 1);
        }

        writer.WriteEndArray();
    }

    private void WritePair(Utf8JsonWriter writer, QueryNode left, QueryNode right, int depth)
    {
        writer.WritePropertyName("left");
        WriteNode(writer, left, depth + 1);
        writer.WritePropertyName("right");
        WriteNode(writer, right, depth + 1);
    }

    private void WriteConstant(Utf8JsonWriter writer, QueryConstant constant)
    {
        if (constant is null)
        {
            throw new JsonException("Query document contains a null constant.");
        }

        writer.WriteStartObject();
        switch (constant)
        {
            case NullConstant:
                writer.WriteString("kind", "null");
                break;
            case BooleanConstant boolean:
                writer.WriteString("kind", "boolean");
                writer.WriteBoolean("value", boolean.Value);
                break;
            case Int64Constant number:
                writer.WriteString("kind", "int64");
                writer.WriteNumber("value", number.Value);
                break;
            case StringConstant text:
                writer.WriteString("kind", "string");
                WriteBoundedString(writer, "value", text.Value, "String value");
                break;
            case InstantConstant instant:
                writer.WriteString("kind", "instant");
                writer.WriteNumber("unixSeconds", instant.UnixSeconds);
                break;
            case EnumConstant member:
                writer.WriteString("kind", "enum");
                WriteBoundedString(writer, "type", member.Type, "Enum type");
                WriteBoundedString(writer, "token", member.Value, "Enum value");
                break;
            case TypedIdConstant id:
                writer.WriteString("kind", "typedId");
                writer.WriteString("type", Wire(id.Target));
                WriteBoundedString(writer, "value", id.Value, "Typed ID value");
                break;
            default:
                throw new JsonException(
                    $"Constant '{constant.GetType().Name}' has no v1 wire form.");
        }

        writer.WriteEndObject();
    }

    private void WriteBoundedString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value,
        string description)
    {
        if (QueryJsonWireRules.ScalarLength(value, description) > _limits.MaximumStringLength)
        {
            throw new JsonException("String value exceeds the maximum length.");
        }

        writer.WriteString(propertyName, value);
    }
}

/// <summary>Reads the stable v1 wire form back into a query document.</summary>
internal sealed class QueryDocumentJsonReader
{
    private readonly QueryJsonLimits _limits;
    private int _nodes;

    internal QueryDocumentJsonReader(QueryJsonLimits limits) => _limits = limits;

    internal static QueryTarget ReadTarget(JsonElement element) =>
        element.GetString() switch
        {
            "session" => QueryTarget.Session,
            "window" => QueryTarget.Window,
            "pane" => QueryTarget.Pane,
            "client" => QueryTarget.Client,
            _ => throw new JsonException("Query document names an unknown target."),
        };

    internal QueryNode ReadNode(JsonElement element, int depth)
    {
        if (depth > _limits.MaximumDepth)
        {
            throw new JsonException("Query document exceeds the maximum nesting depth.");
        }

        if (++_nodes > _limits.MaximumNodes)
        {
            throw new JsonException("Query document exceeds the maximum node count.");
        }

        return element.GetProperty("kind").GetString() switch
        {
            "and" => new AndNode([.. ReadOperands(element, depth)]),
            "or" => new OrNode([.. ReadOperands(element, depth)]),
            "not" => new NotNode(ReadNode(element.GetProperty("operand"), depth + 1)),
            "comparison" => ReadComparisonNode(element, depth),
            "regex" => new RegexNode(
                ReadNode(element.GetProperty("input"), depth + 1),
                ReadDialect(element.GetProperty("dialect")),
                ReadPattern(element.GetProperty("pattern")),
                ReadRegexOptions(element.GetProperty("semanticOptions"))),
            "quantifier" => new QuantifierNode(
                ReadQuantifier(element.GetProperty("quantifier")),
                (FieldNode)ReadNode(element.GetProperty("relation"), depth + 1),
                ReadNode(element.GetProperty("predicate"), depth + 1)),
            "field" => new FieldNode(
                ReadTarget(element.GetProperty("target")),
                ReadBoundedString(element.GetProperty("wireName"), "Field wire name")),
            "constant" => new ConstantNode(ReadConstant(element.GetProperty("value"))),
            _ => throw new JsonException("Query document names an unknown node kind."),
        };
    }

    /// <summary>Reads a regex dialect, refusing one this library cannot evaluate.</summary>
    /// <remarks>
    /// The wire form names a dialect so a future reader can tell .NET patterns
    /// from someone else's. Accepting an unknown name would mean evaluating a
    /// pattern under rules it was not written for.
    /// </remarks>
    private static string ReadDialect(JsonElement element)
    {
        string dialect = element.GetString()
            ?? throw new JsonException("Regex names no dialect.");
        return string.Equals(dialect, QueryRegexSemantics.Dialect, StringComparison.Ordinal)
            ? dialect
            : throw new JsonException($"Regex dialect '{dialect}' is not supported.");
    }

    /// <summary>Reads a pattern, bounded by the declared limit.</summary>
    private string ReadPattern(JsonElement element)
    {
        string pattern = element.GetString()
            ?? throw new JsonException("Regex names no pattern.");
        return QueryJsonWireRules.ScalarLength(pattern, "Regex pattern")
            <= _limits.MaximumPatternLength
            ? pattern
            : throw new JsonException("Regex pattern exceeds the maximum length.");
    }

    /// <summary>Reads regex options, refusing bits this library does not define.</summary>
    /// <remarks>
    /// Arrives as a raw integer, so only the bit combinations the writer can
    /// produce are accepted; anything else describes behaviour this library never writes.
    /// </remarks>
    private static System.Text.RegularExpressions.RegexOptions ReadRegexOptions(
        JsonElement element)
    {
        var options = (RegexOptions)element.GetInt32();
        return QueryRegexSemantics.IsSupported(options)
            ? options
            : throw new JsonException("Regex names options this reader does not support.");
    }

    /// <summary>Reads a string constant, bounded by the declared limit.</summary>
    /// <remarks>
    /// Re-checked here because the writer's own limit does not bound documents
    /// produced elsewhere, which are exactly the ones this limit exists for.
    /// </remarks>
    private string ReadBoundedString(JsonElement element, string description = "String value")
    {
        string value = element.GetString()
            ?? throw new JsonException($"{description} is null.");
        return QueryJsonWireRules.ScalarLength(value, description)
            <= _limits.MaximumStringLength
            ? value
            : throw new JsonException("String value exceeds the maximum length.");
    }

    private QueryNode ReadComparisonNode(JsonElement element, int depth)
    {
        string? operation = element.GetProperty("operator").GetString();
        QueryNode left = ReadNode(element.GetProperty("left"), depth + 1);
        QueryNode right = ReadNode(element.GetProperty("right"), depth + 1);
        return operation switch
        {
            "equal" => new ComparisonNode(QueryComparison.Equal, left, right),
            "notEqual" => new ComparisonNode(QueryComparison.NotEqual, left, right),
            "lessThan" => new ComparisonNode(QueryComparison.LessThan, left, right),
            "lessThanOrEqual" =>
                new ComparisonNode(QueryComparison.LessThanOrEqual, left, right),
            "greaterThan" => new ComparisonNode(QueryComparison.GreaterThan, left, right),
            "greaterThanOrEqual" =>
                new ComparisonNode(QueryComparison.GreaterThanOrEqual, left, right),
            "stringEqualOrdinal" =>
                new StringNode(QueryStringOperation.EqualsOrdinal, left, right),
            "stringEqualOrdinalIgnoreCase" =>
                new StringNode(QueryStringOperation.EqualsOrdinalIgnoreCase, left, right),
            "startsWithOrdinal" =>
                new StringNode(QueryStringOperation.StartsWithOrdinal, left, right),
            "endsWithOrdinal" =>
                new StringNode(QueryStringOperation.EndsWithOrdinal, left, right),
            "containsOrdinal" =>
                new StringNode(QueryStringOperation.ContainsOrdinal, left, right),
            _ => throw new JsonException("Query document names an unknown comparison."),
        };
    }

    private static QueryQuantifier ReadQuantifier(JsonElement element) =>
        element.GetString() switch
        {
            "any" => QueryQuantifier.Any,
            "all" => QueryQuantifier.All,
            _ => throw new JsonException("Query document names an unknown quantifier."),
        };

    private QueryConstant ReadConstant(JsonElement element) =>
        element.GetProperty("kind").GetString() switch
        {
            "null" => new NullConstant(),
            "boolean" => new BooleanConstant(element.GetProperty("value").GetBoolean()),
            "int64" => new Int64Constant(element.GetProperty("value").GetInt64()),
            "string" => new StringConstant(ReadBoundedString(element.GetProperty("value"))),
            "instant" => new InstantConstant(element.GetProperty("unixSeconds").GetInt64()),
            "enum" => new EnumConstant(
                ReadBoundedString(element.GetProperty("type"), "Enum type"),
                ReadBoundedString(element.GetProperty("token"), "Enum value")),
            "typedId" => new TypedIdConstant(
                ReadTarget(element.GetProperty("type")),
                ReadBoundedString(element.GetProperty("value"), "Typed ID value")),
            _ => throw new JsonException("Query document names an unknown constant type."),
        };

    private IEnumerable<QueryNode> ReadOperands(JsonElement element, int depth)
    {
        foreach (JsonElement operand in element.GetProperty("operands").EnumerateArray())
        {
            yield return ReadNode(operand, depth + 1);
        }
    }
}
