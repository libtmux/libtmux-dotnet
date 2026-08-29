namespace LibTmux.Query;

internal static class QueryDocumentStructuralGuard
{
    internal const int MaximumDepth = 32;
    internal const int MaximumNodeOccurrences = 512;

    internal static void Validate(QueryNode? predicate, Action? check = null)
    {
        var pending = new Stack<(QueryNode Node, int Depth)>();
        int occurrences = 0;
        Push(predicate, depth: 1);

        while (pending.Count > 0)
        {
            (QueryNode node, int depth) = pending.Pop();
            int childDepth = depth + 1;
            switch (node)
            {
                case AndNode and:
                    PushOperands(and.Operands, childDepth);
                    break;
                case OrNode or:
                    PushOperands(or.Operands, childDepth);
                    break;
                case NotNode not:
                    Push(not.Operand, childDepth);
                    break;
                case ComparisonNode comparison:
                    Push(comparison.Right, childDepth);
                    Push(comparison.Left, childDepth);
                    break;
                case StringNode text:
                    Push(text.Right, childDepth);
                    Push(text.Left, childDepth);
                    break;
                case RegexNode regex:
                    Push(regex.Input, childDepth);
                    break;
                case QuantifierNode quantifier:
                    Push(quantifier.Predicate, childDepth);
                    Push(quantifier.Relation, childDepth);
                    break;
                case ConstantNode constant:
                    ValidateConstant(constant.Value);
                    break;
                case FieldNode:
                    break;
                default:
                    throw Unsupported($"Node '{node.GetType().Name}' is not supported.");
            }
        }

        void PushOperands(IReadOnlyList<QueryNode> operands, int depth)
        {
            for (int index = operands.Count - 1; index >= 0; index--)
            {
                Push(operands[index], depth);
            }
        }

        void Push(QueryNode? node, int depth)
        {
            check?.Invoke();
            if (node is null)
            {
                throw Unsupported("Query document contains a null node.");
            }

            if (depth > MaximumDepth)
            {
                throw Unsupported(
                    $"Query document exceeds the maximum nesting depth of {MaximumDepth}.");
            }

            if (++occurrences > MaximumNodeOccurrences)
            {
                throw Unsupported(
                    "Query document exceeds the maximum node count of "
                    + $"{MaximumNodeOccurrences}.");
            }

            pending.Push((node, depth));
        }
    }

    private static void ValidateConstant(QueryConstant? constant)
    {
        switch (constant)
        {
            case NullConstant:
            case BooleanConstant:
            case Int64Constant:
            case StringConstant:
            case TypedIdConstant:
                return;
            case null:
                throw Unsupported("Query document contains a null constant.");
            default:
                throw Unsupported(
                    $"Constant '{constant.GetType().Name}' is not supported.");
        }
    }

    private static UnsupportedQueryExpressionException Unsupported(string message) => new(message);
}
