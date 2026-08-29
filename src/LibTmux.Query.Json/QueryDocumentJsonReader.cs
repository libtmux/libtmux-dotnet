using System.Text.Json;
using System.Text.RegularExpressions;

namespace LibTmux.Query.Json;

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

        string? kind = element.GetProperty("kind").GetString();
        QueryJsonWireRules.ValidateNode(element, kind);
        return kind switch
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

    private static string ReadDialect(JsonElement element)
    {
        string dialect = element.GetString()
            ?? throw new JsonException("Regex names no dialect.");
        return string.Equals(dialect, QueryRegexSemantics.Dialect, StringComparison.Ordinal)
            ? dialect
            : throw new JsonException($"Regex dialect '{dialect}' is not supported.");
    }

    private string ReadPattern(JsonElement element)
    {
        string pattern = element.GetString()
            ?? throw new JsonException("Regex names no pattern.");
        return QueryJsonWireRules.ScalarLength(pattern, "Regex pattern")
            <= _limits.MaximumPatternLength
            ? pattern
            : throw new JsonException("Regex pattern exceeds the maximum length.");
    }

    private static RegexOptions ReadRegexOptions(JsonElement element)
    {
        var options = (RegexOptions)element.GetInt32();
        return QueryRegexSemantics.IsSupported(options)
            ? options
            : throw new JsonException("Regex names options this reader does not support.");
    }

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

    private QueryConstant ReadConstant(JsonElement element)
    {
        string? kind = element.GetProperty("kind").GetString();
        QueryJsonWireRules.ValidateConstant(element, kind);
        return kind switch
        {
            "null" => new NullConstant(),
            "boolean" => new BooleanConstant(element.GetProperty("value").GetBoolean()),
            "int64" => new Int64Constant(element.GetProperty("value").GetInt64()),
            "string" => new StringConstant(ReadBoundedString(element.GetProperty("value"))),
            "typedId" => new TypedIdConstant(
                ReadTarget(element.GetProperty("type")),
                ReadBoundedString(element.GetProperty("value"), "Typed ID value")),
            _ => throw new JsonException("Query document names an unknown constant type."),
        };
    }

    private IEnumerable<QueryNode> ReadOperands(JsonElement element, int depth)
    {
        foreach (JsonElement operand in element.GetProperty("operands").EnumerateArray())
        {
            yield return ReadNode(operand, depth + 1);
        }
    }
}
