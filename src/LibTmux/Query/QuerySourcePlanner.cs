using LibTmux.Internal;

namespace LibTmux.Query;

internal static class QuerySourcePlanner
{
    private static readonly TmuxVersion LastVerifiedVersion = TmuxVersion.Parse("3.7c");

    internal static QuerySourceParts Prepare<T>(QueryDocument document, TmuxVersion version, QueryPushdown pushdown)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = QueryDocumentValidator.Validate(document);
        RequireNativeTarget<T>(document.Target);
        if (!version.IsValid)
        {
            throw new ArgumentException("A valid observed daemon version is required.", nameof(version));
        }
        if (pushdown is < QueryPushdown.Never or > QueryPushdown.Require)
        {
            throw new ArgumentOutOfRangeException(nameof(pushdown), pushdown, "The query evaluation mode is undefined.");
        }

        FieldNode[] fields = Nodes(document.Predicate).OfType<FieldNode>().Distinct().ToArray();
        QueryFieldDescriptor[] required = [.. fields.Select(field => QueryFieldCatalog.GetFields(field.Target)
            .Single(descriptor => descriptor.WireName == field.WireName))];
        QueryNode? pushed = null;
        QueryNode? residual = document.Predicate;
        string? format = null;
        List<string> reasons = [];
        if (pushdown == QueryPushdown.Never)
        {
            reasons.Add("Source evaluation was disabled by Never.");
        }
        else if (!version.IsStableRelease || version > LastVerifiedVersion
            || !TmuxCapabilities.IsSupported(version, "format_fields_and_operators"))
        {
            reasons.Add("Exact source evaluation is verified only for stable tmux 3.2a through 3.7c.");
        }
        else if (fields.Any(field => QueryFieldCatalog.IsRelation(field.WireName)))
        {
            reasons.Add("Relationship predicates require complete local graph evaluation.");
        }
        else
        {
            (pushed, residual, format) = Split(document.Predicate);
            if (residual is not null)
            {
                reasons.Add("The remaining predicate has no exact source translation at its evaluation position.");
            }
            if (format is not null && !FitsCommand(document.Target, version, format))
            {
                pushed = null;
                residual = document.Predicate;
                format = null;
                reasons.Clear();
                reasons.Add("The framed projection and generation guard exceed the tmux command byte budget.");
            }
        }
        if (pushdown == QueryPushdown.Require && residual is not null)
        {
            throw new UnsupportedQueryExpressionException(string.Join(" ", reasons));
        }
        return new(pushed, residual, format, Array.AsReadOnly(required), reasons.AsReadOnly());
    }

    internal static void RequireNativeTarget<T>(QueryTarget target)
    {
        bool valid = target switch
        {
            QueryTarget.Session => typeof(T) == typeof(Session),
            QueryTarget.Window => typeof(T) == typeof(Window),
            QueryTarget.Pane => typeof(T) == typeof(Pane),
            _ => false,
        };
        if (!valid)
        {
            throw new UnsupportedQueryExpressionException(
                "Source execution requires the document's native Session, Window, or Pane type.");
        }
    }

    internal static string ListCommand(QueryTarget target) => target switch
    {
        QueryTarget.Session => "list-sessions",
        QueryTarget.Window => "list-windows",
        QueryTarget.Pane => "list-panes",
        _ => throw new UnsupportedQueryExpressionException("This target has no hierarchy source."),
    };

    private static bool FitsCommand(QueryTarget target, TmuxVersion version, string format)
    {
        FormatProjection projection = FormatProjection.Create(ListCommand(target), version, format);
        string[] command = [projection.ListCommand, .. target == QueryTarget.Session ? Array.Empty<string>() : ["-a"], "-F", projection.Template];
        TmuxCommandRequest guarded = TmuxGenerationGuard.CreateRequest(
            new ServerGeneration(int.MaxValue, long.MaxValue), [command], new string('x', TmuxGenerationGuard.MarkerLength));
        return guarded.FitsNativeArgumentBudget();
    }

    private static (QueryNode? Pushed, QueryNode? Residual, string? Format) Split(QueryNode node)
    {
        if (Exact(node) is { } all)
        {
            return (node, null, all);
        }
        if (node is not AndNode and)
        {
            return (null, node, null);
        }
        List<QueryNode> prefix = [];
        List<string> formats = [];
        foreach (QueryNode operand in and.Operands)
        {
            if (Exact(operand) is not { } format)
            {
                break;
            }
            prefix.Add(operand);
            formats.Add(format);
        }
        return prefix.Count == 0
            ? (null, node, null)
            : (Conjunction(prefix), Conjunction(and.Operands.Skip(prefix.Count).ToArray()), Combine(formats, "&&"));
    }

    private static QueryNode Conjunction(IReadOnlyList<QueryNode> nodes) =>
        nodes.Count == 1 ? nodes[0] : new AndNode(nodes);

    private static string? Exact(QueryNode node) => node switch
    {
        ConstantNode { Value: BooleanConstant literal } => literal.Value ? "1" : "0",
        FieldNode field => Boolean(field.WireName),
        ComparisonNode comparison => Comparison(comparison),
        NotNode not when Exact(not.Operand) is { } operand => $"#{{!:{operand}}}",
        AndNode and => ExactOperands(and.Operands, "&&"),
        OrNode or => ExactOperands(or.Operands, "||"),
        _ => null,
    };

    private static string? Comparison(ComparisonNode node)
    {
        if (node.Operator is not (QueryComparison.Equal or QueryComparison.NotEqual)
            || node.Left is not FieldNode field || node.Right is not ConstantNode constant)
        {
            return null;
        }
        string operation = node.Operator == QueryComparison.Equal ? "==" : "!=";
        if (constant.Value is BooleanConstant boolean && Boolean(field.WireName) is { } value)
        {
            return $"#{{{operation}:{value},{(boolean.Value ? "1" : "0")}}}";
        }
        if (constant.Value is TypedIdConstant id && Canonical(id)
            && field.WireName is "session_id" or "window_id" or "pane_id")
        {
            return $"#{{{operation}:#{{{field.WireName}}},{id.Value}}}";
        }
        return null;
    }

    private static string? Boolean(string name) => name switch
    {
        "session_attached" => "#{!=:#{session_attached},0}",
        "window_active" => "#{window_active}",
        _ => null,
    };

    private static bool Canonical(TypedIdConstant id) => id.Target switch
    {
        QueryTarget.Session => SessionId.TryParse(id.Value, out SessionId parsed) && parsed.ToString() == id.Value,
        QueryTarget.Window => WindowId.TryParse(id.Value, out WindowId parsed) && parsed.ToString() == id.Value,
        QueryTarget.Pane => PaneId.TryParse(id.Value, out PaneId parsed) && parsed.ToString() == id.Value,
        _ => false,
    };

    private static string? ExactOperands(IReadOnlyList<QueryNode> operands, string operation)
    {
        List<string> formats = [];
        foreach (QueryNode operand in operands)
        {
            if (Exact(operand) is not { } format)
            {
                return null;
            }
            formats.Add(format);
        }
        return Combine(formats, operation);
    }

    private static string Combine(List<string> operands, string operation)
    {
        return Fold(0, operands.Count);

        // A balanced format avoids tmux's recursion limit for a wide query.
        string Fold(int offset, int count) => count switch
        {
            0 => operation == "&&" ? "1" : "0",
            1 => operands[offset],
            _ => $"#{{{operation}:{Fold(offset, count / 2)},{Fold(offset + count / 2, count - count / 2)}}}",
        };
    }

    private static IEnumerable<QueryNode> Nodes(QueryNode node)
    {
        yield return node;
        IEnumerable<QueryNode> children = node switch
        {
            AndNode and => and.Operands,
            OrNode or => or.Operands,
            NotNode not => [not.Operand],
            ComparisonNode comparison => [comparison.Left, comparison.Right],
            StringNode text => [text.Left, text.Right],
            RegexNode regex => [regex.Input],
            QuantifierNode quantifier => [quantifier.Relation, quantifier.Predicate],
            RelatedNode related => [related.Relation, related.Predicate],
            _ => [],
        };
        foreach (QueryNode child in children)
        {
            foreach (QueryNode descendant in Nodes(child))
            {
                yield return descendant;
            }
        }
    }
}

internal sealed record QuerySourceParts(QueryNode? Pushed, QueryNode? Residual, string? Format,
    IReadOnlyList<QueryFieldDescriptor> Fields, IReadOnlyList<string> Reasons);
