using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Translates a supported expression into a query document.</summary>
/// <remarks>
/// Translation is total or it fails. Every node the vocabulary does not cover
/// raises <see cref="UnsupportedQueryExpressionException" /> rather than being
/// left for in-memory evaluation, so one predicate cannot mean two things.
/// </remarks>
internal static class QueryTranslator
{
    internal static QueryDocument Translate<T>(
        Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ParameterExpression parameter = predicate.Parameters[0];
        QueryNode node = TranslateNode(predicate.Body, parameter);
        QueryDocument document = new(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            TargetOf(node),
            node);
        QueryDocumentValidator.Validate(document);
        return document;
    }

    private static QueryNode TranslateNode(Expression body, ParameterExpression parameter) =>
        body switch
        {
            BinaryExpression { NodeType: ExpressionType.AndAlso } and =>
                new AndNode([.. Flatten(and, ExpressionType.AndAlso, parameter)]),
            BinaryExpression { NodeType: ExpressionType.OrElse } or =>
                new OrNode([.. Flatten(or, ExpressionType.OrElse, parameter)]),
            UnaryExpression { NodeType: ExpressionType.Not } not =>
                new NotNode(TranslateNode(not.Operand, parameter)),
            _ => TranslateAtomicNode(body, parameter),
        };

    private static QueryNode TranslateAtomicNode(Expression body, ParameterExpression parameter)
    {
        Expression? operand = body switch
        {
            BinaryExpression binary => binary.Left,
            MethodCallExpression call => call.Object ?? call.Arguments.FirstOrDefault(),
            MemberExpression member => member,
            _ => null,
        };
        if (operand is not null
            && TryFindNavigation(operand, parameter, out Expression navigation, out FieldNode relation))
        {
            ParameterExpression child = Expression.Parameter(navigation.Type, "related");
            Expression rewritten = new NavigationRewriter(navigation, child).Visit(body)!;
            return new RelatedNode(relation, TranslateNode(rewritten, child));
        }

        return body switch
        {
            BinaryExpression binary => TranslateBinary(binary, parameter),
            MethodCallExpression call => TranslateCall(call, parameter),
            MemberExpression member when member.Type == typeof(bool) =>
                new ComparisonNode(
                    QueryComparison.Equal,
                    TranslateOperand(member, parameter),
                    new ConstantNode(new BooleanConstant(true))),
            _ => throw Unsupported(body),
        };
    }

    private static bool TryFindNavigation(
        Expression expression,
        ParameterExpression parameter,
        out Expression navigation,
        out FieldNode relation)
    {
        if (StripConvert(expression) is MemberExpression member)
        {
            if (member.Member.Name == nameof(CapturedValue<object>.Value)
                && member.Expression is MemberExpression captured
                && captured.Expression == parameter
                && IsCapturedValue(captured.Type)
                && TrySingleRelation(captured.Member, out relation))
            {
                navigation = member;
                return true;
            }

            if (member.Expression == parameter
                && !IsCapturedValue(member.Type)
                && TrySingleRelation(member.Member, out relation))
            {
                navigation = member;
                return true;
            }

            if (member.Expression is not null
                && TryFindNavigation(member.Expression, parameter, out navigation, out relation))
            {
                return true;
            }
        }

        navigation = null!;
        relation = null!;
        return false;
    }

    private static bool IsCapturedValue(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CapturedValue<>);

    private static bool TrySingleRelation(MemberInfo member, out FieldNode relation)
    {
        string wireName = WireName(member);
        if (QueryFieldCatalog.TryGetRelation(wireName, out QueryRelationDefinition shape)
            && shape.Cardinality == QueryRelationCardinality.One
            && QueryFieldCatalog.TryGetTarget(wireName, out QueryTarget target))
        {
            relation = new FieldNode(target, wireName);
            return true;
        }

        relation = null!;
        return false;
    }

    private sealed class NavigationRewriter(Expression navigation, ParameterExpression child)
        : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) =>
            node == navigation ? child : base.Visit(node);
    }

    private static IEnumerable<QueryNode> Flatten(
        BinaryExpression binary,
        ExpressionType kind,
        ParameterExpression parameter)
    {
        // C# parses a && b && c left-associatively; a flat operand list keeps
        // the document shape independent of that accident.
        foreach (Expression side in new[] { binary.Left, binary.Right })
        {
            if (side is BinaryExpression nested && nested.NodeType == kind)
            {
                foreach (QueryNode operand in Flatten(nested, kind, parameter))
                {
                    yield return operand;
                }
            }
            else
            {
                yield return TranslateNode(side, parameter);
            }
        }
    }

    private static QueryNode TranslateBinary(
        BinaryExpression binary,
        ParameterExpression parameter)
    {
        QueryComparison comparison = binary.NodeType switch
        {
            ExpressionType.Equal => QueryComparison.Equal,
            ExpressionType.NotEqual => QueryComparison.NotEqual,
            ExpressionType.LessThan => QueryComparison.LessThan,
            ExpressionType.LessThanOrEqual => QueryComparison.LessThanOrEqual,
            ExpressionType.GreaterThan => QueryComparison.GreaterThan,
            ExpressionType.GreaterThanOrEqual => QueryComparison.GreaterThanOrEqual,
            _ => throw Unsupported(binary),
        };
        QueryNode left = TranslateOperand(binary.Left, parameter);
        QueryNode right = TranslateOperand(binary.Right, parameter);
        if (comparison is QueryComparison.Equal or QueryComparison.NotEqual
            && left is FieldNode field
            && right is ConstantNode constant
            && QueryFieldCatalog.TryGetKind(field.WireName, out QueryValueKind kind))
        {
            if (kind == QueryValueKind.String && constant.Value is StringConstant)
            {
                StringNode equality = new(QueryStringOperation.EqualsOrdinal, left, right);
                return comparison == QueryComparison.Equal ? equality : new NotNode(equality);
            }

            if (kind == QueryValueKind.TypedId && constant.Value is StringConstant id)
            {
                right = new ConstantNode(new TypedIdConstant(field.Target, id.Value));
            }
        }

        return new ComparisonNode(comparison, left, right);
    }

    private static QueryNode TranslateCall(
        MethodCallExpression call,
        ParameterExpression parameter)
    {
        if (call.Method.DeclaringType == typeof(Regex) && call.Method.Name == "IsMatch")
        {
            return TranslateRegex(call, parameter);
        }

        if (call.Method.DeclaringType == typeof(Enumerable)
            && call.Method.Name is "Any" or "All"
            && (call.Arguments.Count == 2 || call.Method.Name == "Any" && call.Arguments.Count == 1))
        {
            return TranslateQuantifier(call, parameter);
        }

        QueryStringOperation operation = call.Method.Name switch
        {
            "StartsWith" => QueryStringOperation.StartsWithOrdinal,
            "EndsWith" => QueryStringOperation.EndsWithOrdinal,
            "Contains" => QueryStringOperation.ContainsOrdinal,
            _ => throw Unsupported(call),
        };
        if (call.Method.DeclaringType != typeof(string) || call.Object is null)
        {
            throw Unsupported(call);
        }

        if (call.Arguments.Count == 1)
        {
            // Contains(string) is ordinal. The one-argument StartsWith and
            // EndsWith overloads use the current culture and have no portable wire form.
            if (operation != QueryStringOperation.ContainsOrdinal)
            {
                throw Unsupported(call);
            }
        }
        else if (call.Arguments.Count != 2
            || !TryConstant(call.Arguments[1], out object? comparison)
            || comparison is not StringComparison.Ordinal)
        {
            throw Unsupported(call);
        }

        return new StringNode(
            operation,
            TranslateOperand(call.Object, parameter),
            TranslateOperand(call.Arguments[0], parameter));
    }

    private static RegexNode TranslateRegex(
        MethodCallExpression call,
        ParameterExpression parameter)
    {
        if (call.Arguments.Count is not (2 or 3)
            || !TryConstant(call.Arguments[1], out object? pattern))
        {
            // A non-constant pattern or explicit timeout cannot be carried on
            // the wire, and dropping either would change the predicate.
            throw Unsupported(call);
        }

        RegexOptions options = RegexOptions.None;
        if (call.Arguments.Count > 2)
        {
            if (!TryConstant(call.Arguments[2], out object? raw) || raw is not RegexOptions parsed)
            {
                throw Unsupported(call);
            }

            options = parsed;
        }

        if (!QueryRegexSemantics.IsSupported(options))
        {
            throw Unsupported(call);
        }

        return new RegexNode(
            TranslateOperand(call.Arguments[0], parameter),
            QueryRegexSemantics.Dialect,
            (string)pattern!,
            options);
    }

    private static QuantifierNode TranslateQuantifier(
        MethodCallExpression call,
        ParameterExpression parameter)
    {
        if (TranslateOperand(call.Arguments[0], parameter) is not FieldNode relation
            || !QueryFieldCatalog.TryGetRelation(relation.WireName, out QueryRelationDefinition shape)
            || shape.Cardinality != QueryRelationCardinality.Many)
        {
            throw Unsupported(call);
        }

        if (call.Arguments.Count == 1)
        {
            return new QuantifierNode(QueryQuantifier.Any, relation, new ConstantNode(new BooleanConstant(true)));
        }

        if (StripQuotes(call.Arguments[1]) is not LambdaExpression lambda)
        {
            throw Unsupported(call);
        }

        return new QuantifierNode(
            call.Method.Name == "Any" ? QueryQuantifier.Any : QueryQuantifier.All,
            relation,
            TranslateNode(lambda.Body, lambda.Parameters[0]));
    }

    private static QueryNode TranslateOperand(
        Expression operand,
        ParameterExpression parameter)
    {
        Expression stripped = StripConvert(operand);
        if (stripped is MemberExpression member
            && StripConvert(member.Expression ?? stripped) == parameter)
        {
            return FieldFor(member.Member);
        }

        if (stripped is MemberExpression { Member.Name: "Count", Expression: MemberExpression collection }
            && collection.Expression == parameter
            && QueryFieldCatalog.TryGetRelation(WireName(collection.Member), out QueryRelationDefinition shape)
            && shape.Cardinality == QueryRelationCardinality.Many)
        {
            return FieldFor(collection.Member);
        }

        return TryConstant(stripped, out object? value)
            ? new ConstantNode(ConstantFor(value, stripped.Type))
            : throw Unsupported(operand);
    }

    private static FieldNode FieldFor(MemberInfo member)
    {
        // Entity properties use cataloged tmux names. Caller-defined
        // projections already name their wire fields.
        string wireName = WireName(member);
        // The catalog is closed: a field it does not carry has no wire form.
        if (!QueryFieldCatalog.TryGetTarget(wireName, out QueryTarget target))
        {
            throw new UnsupportedQueryExpressionException(
                $"Field '{wireName}' is outside the queryable field catalog.");
        }

        return new FieldNode(target, wireName);
    }

    private static string WireName(MemberInfo member) =>
        member.DeclaringType is { } owner
        && QueryFieldCatalog.TryGetWireName(owner, member.Name, out string mapped)
            ? mapped
            : ToWireName(member.Name);

    private static QueryConstant ConstantFor(object? value, Type declared) => value switch
    {
        null => new NullConstant(),
        bool boolean => new BooleanConstant(boolean),
        string text => new StringConstant(text),
        SessionId id => new TypedIdConstant(QueryTarget.Session, id.ToString()),
        WindowId id => new TypedIdConstant(QueryTarget.Window, id.ToString()),
        PaneId id => new TypedIdConstant(QueryTarget.Pane, id.ToString()),
        sbyte number => new Int64Constant(number),
        byte number => new Int64Constant(number),
        short number => new Int64Constant(number),
        ushort number => new Int64Constant(number),
        int number => new Int64Constant(number),
        uint number => new Int64Constant(number),
        long number => new Int64Constant(number),
        ulong number when number <= long.MaxValue => new Int64Constant((long)number),
        _ => throw new UnsupportedQueryExpressionException(
            $"Constant of type '{declared.Name}' has no wire form."),
    };

    private static bool TryConstant(Expression expression, out object? value)
    {
        if (expression is ConstantExpression constant)
        {
            value = constant.Value;
            return true;
        }

        if (expression is MemberExpression
            {
                Expression: ConstantExpression { Value: not null } closure,
                Member: FieldInfo { IsStatic: false } field,
            }
            && field.DeclaringType?.IsDefined(
                typeof(CompilerGeneratedAttribute),
                inherit: false) == true)
        {
            value = field.GetValue(closure.Value);
            return true;
        }

        value = null;
        return false;
    }

    private static Expression StripConvert(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? StripConvert(convert.Operand)
            : expression;

    private static Expression StripQuotes(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote
            ? StripQuotes(quote.Operand)
            : expression;

    private static QueryTarget TargetOf(QueryNode node) => node switch
    {
        FieldNode field => field.Target,
        AndNode and => and.Operands.Select(TargetOf).Min(),
        OrNode or => or.Operands.Select(TargetOf).Min(),
        NotNode not => TargetOf(not.Operand),
        ComparisonNode comparison => Narrower(comparison.Left, comparison.Right),
        StringNode text => Narrower(text.Left, text.Right),
        RegexNode regex => TargetOf(regex.Input),
        QuantifierNode quantifier => quantifier.Relation.Target,
        RelatedNode related => related.Relation.Target,
        _ => QueryTarget.Session,
    };

    private static QueryTarget Narrower(QueryNode left, QueryNode right) =>
        left is FieldNode field ? field.Target : TargetOf(right);

    private static string ToWireName(string clrName)
    {
        var wire = new System.Text.StringBuilder(clrName.Length + 4);
        for (int index = 0; index < clrName.Length; index++)
        {
            if (index > 0 && char.IsUpper(clrName[index]))
            {
                wire.Append('_');
            }

            wire.Append(char.ToLowerInvariant(clrName[index]));
        }

        return wire.ToString();
    }

    private static UnsupportedQueryExpressionException Unsupported(Expression expression) =>
        new(
            $"Expression '{expression}' is outside the supported query vocabulary.",
            expression.ToString());
}
