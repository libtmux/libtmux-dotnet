using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Evaluates a query document against in-memory elements.</summary>
/// <remarks>
/// The interpreter is the semantic owner: translation defines the shape and
/// this defines what the shape means. Compiling from the document rather than
/// from the original expression is what guarantees the in-memory answer
/// matches the wire answer.
/// </remarks>
internal static class QueryInterpreter
{
    internal const string TrimmingMessage =
        "Compiling a query reads public properties by name. Trimmed applications must preserve the filtered types' public properties.";

    [RequiresUnreferencedCode(TrimmingMessage)]
    internal static Func<T, bool> Compile<T>(QueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        QueryDocumentValidator.Validate(document);
        return element => Evaluate(document.Predicate, element!);
    }

    private static bool Evaluate(QueryNode node, object element) => node switch
    {
        AndNode and => and.Operands.All(operand => Evaluate(operand, element)),
        OrNode or => or.Operands.Any(operand => Evaluate(operand, element)),
        NotNode not => !Evaluate(not.Operand, element),
        ComparisonNode comparison => Compare(comparison, element),
        StringNode text => CompareText(text, element),
        RegexNode regex => Regex.IsMatch(
            ReadText(regex.Input, element) ?? string.Empty,
            regex.Pattern,
            regex.SemanticOptions,
            QueryRegexSemantics.MatchTimeout),
        QuantifierNode quantifier => Quantify(quantifier, element),
        FieldNode field => ReadBoolean(field, element),
        ConstantNode { Value: BooleanConstant boolean } => boolean.Value,
        _ => throw new UnsupportedQueryExpressionException(
            $"Node '{node.GetType().Name}' has no interpretation."),
    };

    private static bool Quantify(QuantifierNode quantifier, object element)
    {
        object? relation = Read(quantifier.Relation, element);
        IEnumerable<object> children = relation is System.Collections.IEnumerable sequence
            ? sequence.Cast<object>()
            : [];
        // Any over nothing is false and All over nothing is true, matching both
        // the design spec and LINQ.
        return quantifier.Quantifier == QueryQuantifier.Any
            ? children.Any(child => Evaluate(quantifier.Predicate, child))
            : children.All(child => Evaluate(quantifier.Predicate, child));
    }

    private static bool Compare(ComparisonNode comparison, object element)
    {
        object? left = Read(comparison.Left, element);
        object? right = Read(comparison.Right, element);
        if (left is null || right is null)
        {
            return comparison.Operator switch
            {
                QueryComparison.Equal => left is null && right is null,
                QueryComparison.NotEqual => (left is null) != (right is null),
                // An ordering against an absent value has no answer, so it is
                // false rather than an arbitrary side.
                _ => false,
            };
        }

        if (comparison.Operator is QueryComparison.Equal or QueryComparison.NotEqual
            && (left is string || right is string))
        {
            bool equal = string.Equals(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            return comparison.Operator == QueryComparison.Equal ? equal : !equal;
        }

        int order = comparison.Left is FieldNode field
            && QueryFieldCatalog.TryGetKind(field.WireName, out QueryValueKind kind)
            && kind == QueryValueKind.Int64
                ? ReadInt64(field, left).CompareTo(ReadInt64(field, right))
                : Comparer<object>.Default.Compare(left, right);
        return comparison.Operator switch
        {
            QueryComparison.Equal => order == 0,
            QueryComparison.NotEqual => order != 0,
            QueryComparison.LessThan => order < 0,
            QueryComparison.LessThanOrEqual => order <= 0,
            QueryComparison.GreaterThan => order > 0,
            QueryComparison.GreaterThanOrEqual => order >= 0,
            _ => false,
        };
    }

    private static long ReadInt64(FieldNode field, object value) => value switch
    {
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number when number <= long.MaxValue => (long)number,
        _ => throw new UnsupportedQueryExpressionException(
            $"Field '{field.WireName}' did not produce an integer value."),
    };

    private static bool CompareText(StringNode text, object element)
    {
        string left = ReadText(text.Left, element) ?? string.Empty;
        string right = ReadText(text.Right, element) ?? string.Empty;
        return text.Operator switch
        {
            QueryStringOperation.EqualsOrdinal =>
                string.Equals(left, right, StringComparison.Ordinal),
            QueryStringOperation.EqualsOrdinalIgnoreCase =>
                string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            QueryStringOperation.StartsWithOrdinal =>
                left.StartsWith(right, StringComparison.Ordinal),
            QueryStringOperation.EndsWithOrdinal =>
                left.EndsWith(right, StringComparison.Ordinal),
            QueryStringOperation.ContainsOrdinal =>
                left.Contains(right, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string? ReadText(QueryNode node, object element) =>
        Read(node, element) is object value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static bool ReadBoolean(FieldNode field, object element) =>
        Read(field, element) is bool value
            ? value
            : throw new UnsupportedQueryExpressionException(
                $"Field '{field.WireName}' did not produce a Boolean value.");

    private static object? Read(QueryNode node, object element) => node switch
    {
        ConstantNode constant => Literal(constant.Value),
        FieldNode field => ReadMember(field, element),
        _ => throw new UnsupportedQueryExpressionException(
            $"Node '{node.GetType().Name}' is not an operand."),
    };

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:Members might be removed",
        Justification = "Every public compilation entry point declares the metadata requirement.")]
    private static object? ReadMember(FieldNode field, object element)
    {
        Type type = element.GetType();

        // A document can be deserialized from elsewhere, so a FieldNode may
        // not be one this library minted. Resolving an unknown wire name by
        // convention would let a forged node read any public property, so the
        // name is checked against the catalog first.
        if (!QueryFieldCatalog.TryGetTarget(field.WireName, out _))
        {
            throw new UnsupportedQueryExpressionException(
                $"Field '{field.WireName}' is not in the query catalog.");
        }

        // Reading has to resolve the same pair translating wrote: an entity
        // holds session_attached under Attached, and a row a caller declared
        // holds it under SessionAttached.
        string property =
            QueryFieldCatalog.TryGetProperty(type, field.WireName, out string mapped)
                ? mapped
                : ToClrName(field.WireName);

        var member = type.GetProperty(property);
        if (member is null)
        {
            throw new UnsupportedQueryExpressionException(
                $"Element exposes no member for field '{field.WireName}'.");
        }

        return member.GetValue(element);
    }

    private static object? Literal(QueryConstant constant) => constant switch
    {
        NullConstant => null,
        BooleanConstant boolean => boolean.Value,
        Int64Constant number => number.Value,
        StringConstant text => text.Value,
        InstantConstant instant => instant.UnixSeconds,
        EnumConstant member => member.Value,
        TypedIdConstant id => id.Value,
        _ => throw new UnsupportedQueryExpressionException(
            $"Constant '{constant.GetType().Name}' has no value."),
    };

    private static string ToClrName(string wireName) =>
        string.Concat(
            wireName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
}
