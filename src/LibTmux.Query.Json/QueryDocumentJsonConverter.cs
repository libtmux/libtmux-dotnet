using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibTmux.Query.Json;

/// <summary>Writes the stable v1 wire form of a query document.</summary>
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
        try
        {
            QueryDocumentValidator.Validate(value);
        }
        catch (UnsupportedQueryExpressionException exception)
        {
            throw new JsonException(exception.Message, exception);
        }

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
