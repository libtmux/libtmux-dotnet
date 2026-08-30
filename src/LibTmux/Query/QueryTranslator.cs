using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Translates a supported expression into a query document.</summary>
/// <remarks>
/// Translation is total or it fails. Every node the vocabulary does not cover
/// raises <see cref="UnsupportedQueryExpressionException" /> rather than being
/// left for in-memory evaluation, so one predicate cannot mean two things.
/// </remarks>
// Reading a captured value out of an expression means running the code that
// produced it, and running code an expression describes needs the runtime to
// generate it. Ahead-of-time publishing cannot, so every caller is told.
[RequiresDynamicCode(
    "Translating an expression evaluates its captured values, which needs runtime code generation.")]
internal static class QueryTranslator
{
    internal static QueryDocument Translate<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ParameterExpression parameter = predicate.Parameters[0];
        QueryNode node = TranslateNode(predicate.Body, parameter);
        return new QueryDocument(
            QueryDocument.CurrentSchema,
            QueryDocument.CurrentVersion,
            TargetOf(node),
            node);
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
            BinaryExpression binary => TranslateBinary(binary, parameter),
            MethodCallExpression call => TranslateCall(call, parameter),
            MemberExpression member when member.Type == typeof(bool) =>
                new ComparisonNode(
                    QueryComparison.Equal,
                    TranslateOperand(member, parameter),
                    new ConstantNode(new BooleanConstant(true))),
            _ => throw Unsupported(body),
        };

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
            && StripConvert(binary.Left).Type == typeof(string))
        {
            StringNode equality = new(QueryStringOperation.EqualsOrdinal, left, right);
            return comparison == QueryComparison.Equal ? equality : new NotNode(equality);
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
            && call.Arguments.Count == 2)
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
        // The wire form is ordinal, so only the overload naming
        // StringComparison.Ordinal is accepted -- the same one CA1310 asks
        // callers to write. A culture-sensitive overload has no wire form and
        // throws instead of silently meaning something else.
        if (call.Object is null || call.Arguments.Count is not (1 or 2))
        {
            throw Unsupported(call);
        }

        if (call.Arguments.Count == 2
            && !(TryConstant(call.Arguments[1], out object? comparison)
                && comparison is StringComparison.Ordinal))
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
        if (call.Arguments.Count < 2 || !TryConstant(call.Arguments[1], out object? pattern))
        {
            // A non-constant pattern cannot be carried on the wire, and
            // compiling it locally would diverge from the document.
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

        return new RegexNode(
            TranslateOperand(call.Arguments[0], parameter),
            "dotnet",
            (string)pattern!,
            options);
    }

    private static QuantifierNode TranslateQuantifier(
        MethodCallExpression call,
        ParameterExpression parameter)
    {
        if (TranslateOperand(call.Arguments[0], parameter) is not FieldNode relation
            || !QueryFieldCatalog.IsRelation(relation.WireName))
        {
            throw Unsupported(call);
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

        return TryConstant(stripped, out object? value)
            ? new ConstantNode(ConstantFor(value, stripped.Type))
            : throw Unsupported(operand);
    }

    private static FieldNode FieldFor(MemberInfo member)
    {
        // What tmux calls a field is not a transformation of what C# calls
        // it -- Client.IsControlClient is client_control_mode, not
        // is_control_client. The catalog carries that pairing; an unknown
        // type is a caller's own row, whose property names are wire names already.
        string wireName =
            member.DeclaringType is { } owner
            && QueryFieldCatalog.TryGetWireName(owner.Name, member.Name, out string mapped)
                ? mapped
                : ToWireName(member.Name);
        // The catalog is closed: a field it does not carry cannot be put on the
        // wire, so translating it would produce a document tmux cannot answer.
        if (!QueryFieldCatalog.TryGetTarget(wireName, out QueryTarget target))
        {
            throw new UnsupportedQueryExpressionException(
                $"Field '{wireName}' is outside the queryable field catalog.");
        }

        return new FieldNode(target, wireName);
    }

    private static QueryConstant ConstantFor(object? value, Type declared) => value switch
    {
        null => new NullConstant(),
        bool boolean => new BooleanConstant(boolean),
        string text => new StringConstant(text),
        DateTimeOffset instant => new InstantConstant(instant.ToUnixTimeSeconds()),
        Enum member => new EnumConstant(declared.Name, member.ToString()),
        SessionId id => new TypedIdConstant(QueryTarget.Session, id.ToString()),
        WindowId id => new TypedIdConstant(QueryTarget.Window, id.ToString()),
        PaneId id => new TypedIdConstant(QueryTarget.Pane, id.ToString()),
        _ when value is IConvertible convertible => new Int64Constant(
            convertible.ToInt64(CultureInfo.InvariantCulture)),
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

        try
        {
            value = Expression.Lambda(Expression.Convert(expression, typeof(object)))
                .Compile()
                .DynamicInvoke();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = null;
            return false;
        }
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
