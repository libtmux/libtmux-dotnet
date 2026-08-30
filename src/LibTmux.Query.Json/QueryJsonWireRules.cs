using System.Text.Json;

namespace LibTmux.Query.Json;

internal static class QueryJsonWireRules
{
    private static readonly string[] EnvelopeProperties =
        ["schema", "version", "target", "predicate"];
    private static readonly string[] FieldProperties = ["kind", "target", "wireName"];
    private static readonly string[] ConstantNodeProperties = ["kind", "value"];
    private static readonly string[] OperandsProperties = ["kind", "operands"];
    private static readonly string[] NotProperties = ["kind", "operand"];
    private static readonly string[] ComparisonProperties =
        ["kind", "operator", "left", "right"];
    private static readonly string[] QuantifierProperties =
        ["kind", "quantifier", "relation", "predicate"];
    private static readonly string[] RegexProperties =
        ["kind", "input", "dialect", "pattern", "semanticOptions"];
    private static readonly string[] KindProperties = ["kind"];
    private static readonly string[] ValueProperties = ["kind", "value"];
    private static readonly string[] TypedIdProperties = ["kind", "type", "value"];

    internal static void ValidateEnvelope(JsonElement element) =>
        ValidateProperties(element, EnvelopeProperties, "query envelope");

    internal static void ValidateNode(JsonElement element, string? kind)
    {
        string[]? allowed = kind switch
        {
            "field" => FieldProperties,
            "constant" => ConstantNodeProperties,
            "and" or "or" => OperandsProperties,
            "not" => NotProperties,
            "comparison" => ComparisonProperties,
            "quantifier" => QuantifierProperties,
            "regex" => RegexProperties,
            _ => null,
        };
        if (allowed is not null)
        {
            ValidateProperties(element, allowed, $"{kind} node");
        }
    }

    internal static void ValidateConstant(JsonElement element, string? kind)
    {
        string[]? allowed = kind switch
        {
            "null" => KindProperties,
            "boolean" or "int64" or "string" => ValueProperties,
            "typedId" => TypedIdProperties,
            _ => null,
        };
        if (allowed is not null)
        {
            ValidateProperties(element, allowed, $"{kind} constant");
        }
    }

    private static void ValidateProperties(
        JsonElement element,
        IReadOnlyList<string> allowed,
        string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Unknown member in {description}.");
            }

            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate member in {description}.");
            }
        }
    }

    internal static int ScalarLength(string? value, string description)
    {
        if (!QueryTextSemantics.TryCountScalars(value, out int scalars))
        {
            throw new JsonException($"{description} is null or contains invalid Unicode.");
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
