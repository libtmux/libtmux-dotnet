using System.Text.RegularExpressions;

namespace LibTmux.Query;

internal static class QueryDocumentValidator
{
    internal static QueryValidationResult Validate(QueryDocument document, Action? check = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.Schema,
                QueryDocument.CurrentSchema,
                StringComparison.Ordinal)
            || document.Version != QueryDocument.CurrentVersion)
        {
            throw Unsupported("Query document names an unknown schema or version.");
        }

        _ = Target(document.Target);
        QueryDocumentStructuralGuard.Validate(document.Predicate, check);
        QueryValidationResult result = new();
        ValidatePredicate(document.Predicate, document.Target, result);
        return result;
    }

    private static void ValidatePredicate(
        QueryNode? node,
        QueryTarget expectedTarget,
        QueryValidationResult result)
    {
        switch (node)
        {
            case FieldNode field when ResolveField(field, expectedTarget) == QueryValueKind.Boolean:
            case ConstantNode { Value: BooleanConstant }:
                return;
            case AndNode and:
                ValidateOperands(and.Operands, expectedTarget, result);
                return;
            case OrNode or:
                ValidateOperands(or.Operands, expectedTarget, result);
                return;
            case NotNode not:
                ValidatePredicate(not.Operand, expectedTarget, result);
                return;
            case ComparisonNode comparison:
                ValidateComparison(comparison, expectedTarget);
                return;
            case StringNode text:
                ValidateString(text, expectedTarget);
                return;
            case RegexNode regex:
                ValidateRegex(regex, expectedTarget, result);
                return;
            case QuantifierNode quantifier:
                ValidateQuantifier(quantifier, expectedTarget, result);
                return;
            case null:
                throw Unsupported("Query predicate is null.");
            default:
                throw Unsupported($"Node '{node.GetType().Name}' is not a Boolean predicate.");
        }
    }

    private static void ValidateOperands(
        IReadOnlyList<QueryNode> operands,
        QueryTarget expectedTarget,
        QueryValidationResult result)
    {
        foreach (QueryNode operand in operands)
        {
            ValidatePredicate(operand, expectedTarget, result);
        }
    }

    private static void ValidateComparison(
        ComparisonNode comparison,
        QueryTarget expectedTarget)
    {
        if (comparison.Left is not FieldNode field
            || comparison.Right is not ConstantNode constant)
        {
            throw Unsupported("Comparison operands must be a field and a constant.");
        }

        QueryValueKind kind = ResolveField(field, expectedTarget);
        ValidateConstant(kind, field.Target, constant.Value);
        switch (comparison.Operator)
        {
            case QueryComparison.Equal:
            case QueryComparison.NotEqual:
                return;
            case QueryComparison.LessThan:
            case QueryComparison.LessThanOrEqual:
            case QueryComparison.GreaterThan:
            case QueryComparison.GreaterThanOrEqual:
                if (kind == QueryValueKind.Int64 && constant.Value is Int64Constant)
                {
                    return;
                }

                throw Unsupported("Ordered comparison requires an integer field.");
            default:
                throw Unsupported("Query document names an unknown comparison.");
        }
    }

    private static void ValidateString(StringNode text, QueryTarget expectedTarget)
    {
        if (text.Left is not FieldNode field
            || text.Right is not ConstantNode { Value: StringConstant constant }
            || ResolveField(field, expectedTarget) != QueryValueKind.String
            || !QueryTextSemantics.TryCountScalars(constant.Value, out _))
        {
            throw Unsupported("String comparison requires a string field and constant.");
        }

        _ = text.Operator switch
        {
            QueryStringOperation.EqualsOrdinal => true,
            QueryStringOperation.EqualsOrdinalIgnoreCase => true,
            QueryStringOperation.StartsWithOrdinal => true,
            QueryStringOperation.EndsWithOrdinal => true,
            QueryStringOperation.ContainsOrdinal => true,
            _ => throw Unsupported("Query document names an unknown string operation."),
        };
    }

    private static void ValidateRegex(
        RegexNode regex,
        QueryTarget expectedTarget,
        QueryValidationResult result)
    {
        if (regex.Input is not FieldNode field
            || ResolveField(field, expectedTarget) != QueryValueKind.String
            || !string.Equals(
                regex.Dialect,
                QueryRegexSemantics.Dialect,
                StringComparison.Ordinal)
            || !QueryTextSemantics.TryCountScalars(regex.Pattern, out int length)
            || length > QueryRegexSemantics.MaximumPatternLength
            || !QueryRegexSemantics.IsSupported(regex.SemanticOptions)
            || !result.TryAddRegex(regex))
        {
            throw Unsupported("Regex does not match the query wire semantics.");
        }
    }

    private static void ValidateQuantifier(
        QuantifierNode quantifier,
        QueryTarget expectedTarget,
        QueryValidationResult result)
    {
        _ = ResolveField(quantifier.Relation, expectedTarget);
        if (quantifier.Quantifier is not QueryQuantifier.Any and not QueryQuantifier.All
            || !QueryFieldCatalog.IsRelation(quantifier.Relation.WireName))
        {
            throw Unsupported("Quantifier does not name a supported relation.");
        }

        QueryTarget childTarget = quantifier.Relation.WireName switch
        {
            "session_windows" => QueryTarget.Window,
            "window_panes" => QueryTarget.Pane,
            _ => throw Unsupported("Quantifier does not name a supported relation."),
        };
        ValidatePredicate(quantifier.Predicate, childTarget, result);
    }

    private static QueryValueKind ResolveField(FieldNode field, QueryTarget expectedTarget)
    {
        if (field.Target != expectedTarget
            || !QueryFieldCatalog.TryGetTarget(field.WireName, out QueryTarget target)
            || target != field.Target
            || !QueryFieldCatalog.TryGetKind(field.WireName, out QueryValueKind kind))
        {
            throw Unsupported($"Field '{field.WireName}' is outside the query catalog.");
        }

        return kind;
    }

    private static void ValidateConstant(
        QueryValueKind kind,
        QueryTarget target,
        QueryConstant? constant)
    {
        bool compatible = constant switch
        {
            NullConstant => true,
            BooleanConstant => kind == QueryValueKind.Boolean,
            Int64Constant => kind == QueryValueKind.Int64,
            StringConstant text =>
                kind == QueryValueKind.String
                && QueryTextSemantics.TryCountScalars(text.Value, out _),
            TypedIdConstant id =>
                kind == QueryValueKind.TypedId
                && id.Target == target
                && QueryTextSemantics.TryCountScalars(id.Value, out _),
            _ => false,
        };
        if (!compatible)
        {
            throw Unsupported("Constant does not match its field.");
        }
    }

    private static QueryTarget Target(QueryTarget target) => target switch
    {
        QueryTarget.Session => target,
        QueryTarget.Window => target,
        QueryTarget.Pane => target,
        QueryTarget.Client => target,
        _ => throw Unsupported("Query document names an unknown target."),
    };

    private static UnsupportedQueryExpressionException Unsupported(string message) => new(message);
}

internal sealed class QueryValidationResult
{
    private readonly Dictionary<RegexNode, Regex> _regexes = [];

    internal int RegexCount => _regexes.Count;

    internal Regex GetRegex(RegexNode node) => _regexes[node];

    internal bool TryAddRegex(RegexNode node)
    {
        if (_regexes.ContainsKey(node))
        {
            return true;
        }

        if (!QueryRegexSemantics.TryCreate(
                node.Pattern,
                node.SemanticOptions,
                out Regex? regex))
        {
            return false;
        }

        _regexes.Add(node, regex);
        return true;
    }
}
