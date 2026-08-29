using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
    internal static Func<T, bool> Compile<T>(QueryDocument document) =>
        Compile<T>(document, out _);

    [RequiresUnreferencedCode(TrimmingMessage)]
    internal static Func<T, bool> Compile<T>(
        QueryDocument document,
        out QueryBindingMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(document);
        QueryValidationResult validation = QueryDocumentValidator.Validate(document);
        QueryPlanBindings bindings = new(validation);
        Func<object, bool> predicate = BindPredicate(document.Predicate, typeof(T), bindings);
        metrics = bindings.Metrics;
        return element => predicate(element!);
    }

    private static Func<object, bool> BindPredicate(
        QueryNode node,
        Type elementType,
        QueryPlanBindings bindings) => node switch
        {
            AndNode and => BindAnd(and, elementType, bindings),
            OrNode or => BindOr(or, elementType, bindings),
            NotNode not => BindNot(not, elementType, bindings),
            ComparisonNode comparison => BindComparison(comparison, elementType, bindings),
            StringNode text => BindText(text, elementType, bindings),
            RegexNode regex => BindRegex(regex, elementType, bindings),
            QuantifierNode quantifier => BindQuantifier(quantifier, elementType, bindings),
            FieldNode field => BindBoolean(field, elementType, bindings),
            ConstantNode { Value: BooleanConstant boolean } => _ => boolean.Value,
            _ => throw new UnsupportedQueryExpressionException(
                $"Node '{node.GetType().Name}' has no interpretation."),
        };

    private static Func<object, bool> BindAnd(
        AndNode and,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, bool>[] operands =
            [.. and.Operands.Select(operand => BindPredicate(operand, elementType, bindings))];
        return element => AllOperands(operands, element);
    }

    private static Func<object, bool> BindOr(
        OrNode or,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, bool>[] operands =
            [.. or.Operands.Select(operand => BindPredicate(operand, elementType, bindings))];
        return element => AnyOperand(operands, element);
    }

    private static bool AllOperands(Func<object, bool>[] operands, object element)
    {
        foreach (Func<object, bool> operand in operands)
        {
            if (!operand(element))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyOperand(Func<object, bool>[] operands, object element)
    {
        foreach (Func<object, bool> operand in operands)
        {
            if (operand(element))
            {
                return true;
            }
        }

        return false;
    }

    private static Func<object, bool> BindNot(
        NotNode not,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, bool> operand = BindPredicate(not.Operand, elementType, bindings);
        return element => !operand(element);
    }

    private static Func<object, bool> BindComparison(
        ComparisonNode comparison,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, object?> leftReader = BindOperand(
            comparison.Left,
            elementType,
            bindings);
        Func<object, object?> rightReader = BindOperand(
            comparison.Right,
            elementType,
            bindings);
        QueryValueKind? kind = comparison.Left is FieldNode field
            && QueryFieldCatalog.TryGetKind(field.WireName, out QueryValueKind resolved)
                ? resolved
                : null;

        return element => Compare(
            comparison,
            kind,
            leftReader(element),
            rightReader(element));
    }

    private static bool Compare(
        ComparisonNode comparison,
        QueryValueKind? kind,
        object? left,
        object? right)
    {
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

        int order = kind == QueryValueKind.Int64
                ? ReadInt64((FieldNode)comparison.Left, left)
                    .CompareTo(ReadInt64((FieldNode)comparison.Left, right))
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

    private static Func<object, bool> BindText(
        StringNode text,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, object?> leftReader = BindOperand(text.Left, elementType, bindings);
        Func<object, object?> rightReader = BindOperand(text.Right, elementType, bindings);
        return element => CompareText(
            text.Operator,
            ReadText(leftReader(element)),
            ReadText(rightReader(element)));
    }

    private static bool CompareText(
        QueryStringOperation operation,
        string? leftValue,
        string? rightValue)
    {
        string left = leftValue ?? string.Empty;
        string right = rightValue ?? string.Empty;
        return operation switch
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

    private static Func<object, bool> BindRegex(
        RegexNode node,
        Type elementType,
        QueryPlanBindings bindings)
    {
        Func<object, object?> input = BindOperand(node.Input, elementType, bindings);
        var regex = bindings.Regex(node);
        return element => regex.IsMatch(ReadText(input(element)) ?? string.Empty);
    }

    private static Func<object, bool> BindQuantifier(
        QuantifierNode quantifier,
        Type elementType,
        QueryPlanBindings bindings)
    {
        QueryFieldAccessor relation = bindings.Field(
            quantifier.Relation,
            elementType,
            QueryFieldRole.Relation);
        Type childType = QueryPlanBindings.RelationElementType(
            quantifier.Relation,
            relation.ValueType);
        Func<object, bool> predicate = BindPredicate(
            quantifier.Predicate,
            childType,
            bindings);

        return quantifier.Quantifier == QueryQuantifier.Any
            ? element => Any(relation.Read(element), predicate)
            : element => All(relation.Read(element), predicate);
    }

    private static bool Any(object? relation, Func<object, bool> predicate)
    {
        if (relation is not System.Collections.IEnumerable children)
        {
            return false;
        }

        foreach (object child in children)
        {
            if (predicate(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool All(object? relation, Func<object, bool> predicate)
    {
        if (relation is not System.Collections.IEnumerable children)
        {
            return true;
        }

        foreach (object child in children)
        {
            if (!predicate(child))
            {
                return false;
            }
        }

        return true;
    }

    private static Func<object, bool> BindBoolean(
        FieldNode field,
        Type elementType,
        QueryPlanBindings bindings)
    {
        QueryFieldAccessor accessor = bindings.Field(
            field,
            elementType,
            QueryFieldRole.Scalar);
        return element => accessor.Read(element) is bool value
            ? value
            : throw new UnsupportedQueryExpressionException(
                $"Field '{field.WireName}' did not produce a Boolean value.");
    }

    private static Func<object, object?> BindOperand(
        QueryNode node,
        Type elementType,
        QueryPlanBindings bindings) =>
        node switch
        {
            ConstantNode constant => BindConstant(constant),
            FieldNode field => bindings.Field(
                field,
                elementType,
                QueryFieldRole.Scalar).Read,
            _ => throw new UnsupportedQueryExpressionException(
                $"Node '{node.GetType().Name}' is not an operand."),
        };

    private static Func<object, object?> BindConstant(ConstantNode constant)
    {
        object? value = Literal(constant.Value);
        return _ => value;
    }

    private static string? ReadText(object? operand) =>
        operand is object value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

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

}
