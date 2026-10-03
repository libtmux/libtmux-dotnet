using System.Globalization;
using System.Text;

namespace LibTmux.Query;

/// <summary>Renders a query predicate as a tmux <c>-f</c> filter that keeps every match.</summary>
/// <remarks>
/// <para>
/// The filter only narrows a listing; the caller rechecks each row locally.
/// Each node therefore has an upper bound, which tmux keeps for every row the
/// predicate accepts, and a lower bound, which tmux keeps only for such rows.
/// Negation swaps them, so an operation tmux cannot evaluate exactly widens a
/// conjunction instead of disabling the whole filter.
/// </para>
/// <para>
/// Case-insensitive and regex operations never reach tmux: its C library folds
/// case differently from .NET ordinal comparison, and it speaks POSIX regex.
/// </para>
/// </remarks>
internal static class TmuxFilterRenderer
{
    private const string True = "1";
    private const string False = "0";

    /// <summary>Renders the upper bound of a validated document.</summary>
    /// <returns>The filter text, or null when tmux cannot narrow the listing.</returns>
    internal static string? Superset(QueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = QueryDocumentValidator.Validate(document);
        string upper = Render(document.Predicate).Upper;
        return upper == True ? null : upper;
    }

    /// <summary>Escapes fnmatch metacharacters; tmux passes no FNM_NOESCAPE.</summary>
    internal static string Glob(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is '*' or '?' or '[' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>Escapes text as a format operand.</summary>
    /// <returns>The operand, or null when the text has no portable spelling.</returns>
    /// <remarks>
    /// An undoubled <c>#(</c> would run a shell command. tmux copies a run of
    /// <c>#</c> before <c>[</c> verbatim as a style marker, so that text has
    /// no spelling at all.
    /// </remarks>
    internal static string? Literal(string value)
    {
        if (value.Contains("#[", StringComparison.Ordinal))
        {
            return null;
        }

        var escaped = new StringBuilder(value.Length + 8);
        foreach (char character in value)
        {
            _ = character switch
            {
                '#' => escaped.Append("##"),
                ',' => escaped.Append("#,"),
                '}' => escaped.Append("#}"),
                _ => escaped.Append(character),
            };
        }

        return escaped.ToString();
    }

    private readonly record struct Bounds(string Upper, string Lower)
    {
        internal static Bounds Unknown { get; } = new(True, False);

        internal static Bounds Exact(string filter) => new(filter, filter);
    }

    private static Bounds Render(QueryNode node) => node switch
    {
        ConstantNode { Value: BooleanConstant { Value: true } } => Bounds.Exact(True),
        ConstantNode { Value: BooleanConstant { Value: false } } => Bounds.Exact(False),
        AndNode and => Fold(and.Operands, And),
        OrNode or => Fold(or.Operands, Or),
        NotNode not => Negate(Render(not.Operand)),
        FieldNode field => Format(field) is { } token ? Bounds.Exact(Truthy(field, token)) : Bounds.Unknown,
        ComparisonNode comparison => Comparison(comparison),
        StringNode text => Text(text),
        QuantifierNode quantifier => Quantifier(quantifier),
        _ => Bounds.Unknown,
    };

    private static Bounds Fold(IReadOnlyList<QueryNode> operands, Func<string, string, string> combine)
    {
        Bounds[] rendered = [.. operands.Select(Render)];
        return new(
            Balanced(rendered, static bounds => bounds.Upper, combine),
            Balanced(rendered, static bounds => bounds.Lower, combine));
    }

    // Before tmux 3.6, && and || take exactly two operands, and a right fold
    // of a long list would approach the format nesting limit.
    private static string Balanced(
        Bounds[] operands,
        Func<Bounds, string> select,
        Func<string, string, string> combine)
    {
        string Build(int first, int count) => count == 1
            ? select(operands[first])
            : combine(Build(first, count / 2), Build(first + (count / 2), count - (count / 2)));

        return Build(0, operands.Length);
    }

    private static string And(string left, string right) => (left, right) switch
    {
        (False, _) or (_, False) => False,
        (True, _) => right,
        (_, True) => left,
        _ => $"#{{&&:{left},{right}}}",
    };

    private static string Or(string left, string right) => (left, right) switch
    {
        (True, _) or (_, True) => True,
        (False, _) => right,
        (_, False) => left,
        _ => $"#{{||:{left},{right}}}",
    };

    // tmux before 3.6 has no #{!:}.
    private static string Not(string operand) => operand switch
    {
        True => False,
        False => True,
        _ => $"#{{?{operand},0,1}}",
    };

    private static Bounds Negate(Bounds operand) => new(Not(operand.Lower), Not(operand.Upper));

    // Session.Attached is any value other than empty or 0, which is tmux's own
    // truth test; the other flags are true only as 1.
    private static string Truthy(FieldNode field, string token) =>
        field.WireName == "session_attached" ? token : $"#{{==:{token},1}}";

    private static Bounds Comparison(ComparisonNode comparison)
    {
        if (comparison.Left is not FieldNode field
            || comparison.Right is not ConstantNode constant
            || Format(field) is not { } token)
        {
            return Bounds.Unknown;
        }

        bool equal = comparison.Operator == QueryComparison.Equal;
        switch (constant.Value)
        {
            case BooleanConstant flag when equal || comparison.Operator == QueryComparison.NotEqual:
                string truthy = Truthy(field, token);
                return Bounds.Exact(flag.Value == equal ? truthy : Not(truthy));
            case TypedIdConstant id when Literal(id.Value) is { } literal
                && (equal || comparison.Operator == QueryComparison.NotEqual):
                string same = $"#{{==:{token},{literal}}}";
                return Bounds.Exact(equal ? same : Not(same));
            case Int64Constant number:
                string operation = comparison.Operator switch
                {
                    QueryComparison.Equal => "==",
                    QueryComparison.NotEqual => "!=",
                    QueryComparison.LessThan => "<",
                    QueryComparison.LessThanOrEqual => "<=",
                    QueryComparison.GreaterThan => ">",
                    _ => ">=",
                };
                string value = number.Value.ToString(CultureInfo.InvariantCulture);
                return Bounds.Exact($"#{{e|{operation}|:{token},{value}}}");
            default:
                return Bounds.Unknown;
        }
    }

    private static Bounds Text(StringNode text)
    {
        if (text.Left is not FieldNode field
            || text.Right is not ConstantNode { Value: StringConstant constant }
            || Format(field) is not { } token)
        {
            return Bounds.Unknown;
        }

        // Local evaluation reads an absent value as empty, as tmux does, so
        // these are exact for an empty operand too.
        string? pattern = text.Operator switch
        {
            QueryStringOperation.StartsWithOrdinal => Glob(constant.Value) + "*",
            QueryStringOperation.EndsWithOrdinal => "*" + Glob(constant.Value),
            QueryStringOperation.ContainsOrdinal => "*" + Glob(constant.Value) + "*",
            _ => null,
        };
        string? filter = text.Operator == QueryStringOperation.EqualsOrdinal
            ? Literal(constant.Value) is { } literal ? $"#{{==:{token},{literal}}}" : null
            : pattern is not null && Literal(pattern) is { } glob ? $"#{{m:{glob},{token}}}" : null;
        return filter is null ? Bounds.Unknown : Bounds.Exact(filter);
    }

    private static Bounds Quantifier(QuantifierNode quantifier)
    {
        // #{W:} walks the row's session's windows and #{P:} the row's window's
        // panes, which are the captured relations the recheck reads.
        string? loop = quantifier.Relation.WireName switch
        {
            "session_windows" => "W",
            "window_panes" => "P",
            _ => null,
        };
        if (loop is null)
        {
            return Bounds.Unknown;
        }

        Bounds child = Render(quantifier.Predicate);
        return quantifier.Quantifier == QueryQuantifier.Any
            ? new(Any(loop, child.Upper), Any(loop, child.Lower))
            : new(All(loop, child.Upper), All(loop, child.Lower));
    }

    // tmux has no empty session or window, so true stays a superset of a
    // local answer that is false for an empty relation.
    private static string Any(string loop, string predicate) => predicate switch
    {
        True or False => predicate,
        _ => $"#{{{loop}:#{{?{predicate},1,}}}}",
    };

    private static string All(string loop, string predicate) => predicate switch
    {
        True or False => predicate,
        _ => Not($"#{{{loop}:#{{?{predicate},,1}}}}"),
    };

    private static string? Format(FieldNode field) =>
        QueryFieldCatalog.TryGetTmuxFormat(field.WireName, out string? format) ? $"#{{{format}}}" : null;
}
