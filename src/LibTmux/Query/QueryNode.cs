using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Names the tmux object a field or quantifier reads.</summary>
public enum QueryTarget
{
    /// <summary>A tmux session.</summary>
    Session = 0,

    /// <summary>A tmux window.</summary>
    Window = 1,

    /// <summary>A tmux pane.</summary>
    Pane = 2,

    /// <summary>A tmux client.</summary>
    Client = 3,
}

/// <summary>Names an ordering or equality comparison.</summary>
public enum QueryComparison
{
    /// <summary>Operands are equal.</summary>
    Equal = 0,

    /// <summary>Operands differ.</summary>
    NotEqual = 1,

    /// <summary>The left operand is smaller.</summary>
    LessThan = 2,

    /// <summary>The left operand is not larger.</summary>
    LessThanOrEqual = 3,

    /// <summary>The left operand is larger.</summary>
    GreaterThan = 4,

    /// <summary>The left operand is not smaller.</summary>
    GreaterThanOrEqual = 5,
}

/// <summary>Names a string comparison, always ordinal.</summary>
/// <remarks>
/// tmux identifiers and names are byte strings, so culture-sensitive
/// comparison would make a query's meaning depend on the caller's locale.
/// </remarks>
public enum QueryStringOperation
{
    /// <summary>Ordinal equality.</summary>
    EqualsOrdinal = 0,

    /// <summary>Case-insensitive ordinal equality.</summary>
    EqualsOrdinalIgnoreCase = 1,

    /// <summary>Ordinal prefix match.</summary>
    StartsWithOrdinal = 2,

    /// <summary>Ordinal suffix match.</summary>
    EndsWithOrdinal = 3,

    /// <summary>Ordinal substring match.</summary>
    ContainsOrdinal = 4,
}

/// <summary>Names how a quantifier folds a relation.</summary>
public enum QueryQuantifier
{
    /// <summary>True when at least one child matches; false when empty.</summary>
    Any = 0,

    /// <summary>True when every child matches; true when empty.</summary>
    All = 1,
}

/// <summary>One node of a translated query predicate.</summary>
public abstract record QueryNode;

/// <summary>One literal value in a query predicate.</summary>
public abstract record QueryConstant;

/// <summary>The absence of a value.</summary>
public sealed record NullConstant : QueryConstant;

/// <summary>A boolean literal.</summary>
/// <param name="Value">The literal value.</param>
public sealed record BooleanConstant(bool Value) : QueryConstant;

/// <summary>A 64-bit integer literal.</summary>
/// <param name="Value">The literal value.</param>
public sealed record Int64Constant(long Value) : QueryConstant;

/// <summary>A string literal.</summary>
/// <param name="Value">The literal value.</param>
public sealed record StringConstant(string Value) : QueryConstant;

/// <summary>A typed tmux identifier literal.</summary>
/// <param name="Target">The object the identifier names.</param>
/// <param name="Value">The identifier text.</param>
public sealed record TypedIdConstant(QueryTarget Target, string Value) : QueryConstant;

/// <summary>A literal operand.</summary>
/// <param name="Value">The literal.</param>
public sealed record ConstantNode(QueryConstant Value) : QueryNode;

/// <summary>A tmux format field operand.</summary>
/// <param name="Target">The object that owns the field.</param>
/// <param name="WireName">The tmux format token name.</param>
public sealed record FieldNode(QueryTarget Target, string WireName) : QueryNode;

/// <summary>An ordering or equality comparison.</summary>
/// <param name="Operator">The comparison.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record ComparisonNode(
    QueryComparison Operator,
    QueryNode Left,
    QueryNode Right) : QueryNode;

/// <summary>An ordinal string comparison.</summary>
/// <param name="Operator">The string operation.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record StringNode(
    QueryStringOperation Operator,
    QueryNode Left,
    QueryNode Right) : QueryNode;

/// <summary>A constant-pattern regular expression match.</summary>
/// <param name="Input">The operand to match.</param>
/// <param name="Dialect">The regex dialect the pattern is written in.</param>
/// <param name="Pattern">The constant pattern.</param>
/// <param name="SemanticOptions">Options that change what the pattern means.</param>
public sealed record RegexNode(
    QueryNode Input,
    string Dialect,
    string Pattern,
    RegexOptions SemanticOptions) : QueryNode;

/// <summary>A quantifier over a relation field.</summary>
/// <param name="Quantifier">How the relation is folded.</param>
/// <param name="Relation">The relation field to fold.</param>
/// <param name="Predicate">The predicate applied to each child.</param>
public sealed record QuantifierNode(
    QueryQuantifier Quantifier,
    FieldNode Relation,
    QueryNode Predicate) : QueryNode;

/// <summary>The negation of one predicate.</summary>
/// <param name="Operand">The negated predicate.</param>
public sealed record NotNode(QueryNode Operand) : QueryNode;

/// <summary>The conjunction of ordered operands.</summary>
/// <remarks>
/// Operand order is part of the value: two conjunctions with the same
/// operands in a different order are different documents, because the
/// wire form preserves order.
/// </remarks>
public sealed record AndNode : QueryNode
{
    /// <summary>Initializes a conjunction.</summary>
    /// <param name="operands">The ordered operands.</param>
    public AndNode(IReadOnlyList<QueryNode> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        Operands = [.. operands];
    }

    /// <summary>Gets the ordered operands.</summary>
    public IReadOnlyList<QueryNode> Operands { get; }

    /// <inheritdoc />
    public bool Equals(AndNode? other) => QueryNodeList.Equal(Operands, other?.Operands);

    /// <inheritdoc />
    public override int GetHashCode() => QueryNodeList.HashCode(Operands);
}

/// <summary>The disjunction of ordered operands.</summary>
/// <remarks>Operand order is part of the value, as for <see cref="AndNode" />.</remarks>
public sealed record OrNode : QueryNode
{
    /// <summary>Initializes a disjunction.</summary>
    /// <param name="operands">The ordered operands.</param>
    public OrNode(IReadOnlyList<QueryNode> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        Operands = [.. operands];
    }

    /// <summary>Gets the ordered operands.</summary>
    public IReadOnlyList<QueryNode> Operands { get; }

    /// <inheritdoc />
    public bool Equals(OrNode? other) => QueryNodeList.Equal(Operands, other?.Operands);

    /// <inheritdoc />
    public override int GetHashCode() => QueryNodeList.HashCode(Operands);
}

internal static class QueryNodeList
{
    // A record's synthesized equality compares list references, which would
    // make two structurally identical documents unequal.
    internal static bool Equal(IReadOnlyList<QueryNode> left, IReadOnlyList<QueryNode>? right) =>
        right is not null && left.SequenceEqual(right);

    internal static int HashCode(IReadOnlyList<QueryNode> operands)
    {
        HashCode hash = default;
        foreach (QueryNode operand in operands)
        {
            hash.Add(operand);
        }

        return hash.ToHashCode();
    }
}
