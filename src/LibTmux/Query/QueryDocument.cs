namespace LibTmux.Query;

/// <summary>One translated query predicate and its wire schema.</summary>
/// <remarks>
/// The document is the stable interchange form. It is produced by translation
/// and never by client evaluation, so an expression that cannot be translated
/// fails loudly rather than silently degrading to in-memory filtering.
/// </remarks>
/// <param name="Schema">The wire schema identifier.</param>
/// <param name="Version">The wire schema version.</param>
/// <param name="Target">The object the predicate selects.</param>
/// <param name="Predicate">The translated predicate.</param>
public sealed record QueryDocument(
    string Schema,
    int Version,
    QueryTarget Target,
    QueryNode Predicate)
{
    /// <summary>The current wire schema identifier.</summary>
    public const string CurrentSchema = "libtmux-query";

    /// <summary>The current wire schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the snapshot depth this predicate needs to evaluate.</summary>
    /// <remarks>
    /// A quantifier over a relation cannot be answered by a shallower capture,
    /// so the depth is derived from the predicate rather than assumed.
    /// </remarks>
    /// <exception cref="UnsupportedQueryExpressionException">
    /// The predicate is malformed or exceeds the version-one structural limits.
    /// </exception>
    public SnapshotDepth RequiredSnapshotDepth
    {
        get
        {
            QueryDocumentStructuralGuard.Validate(Predicate);
            return Depth(Predicate, Target);
        }
    }

    private static SnapshotDepth Depth(QueryNode node, QueryTarget target) => node switch
    {
        QuantifierNode quantifier => Deepest(
            RelationDepth(quantifier.Relation.WireName),
            Depth(quantifier.Predicate, target)),
        AndNode and => and.Operands.Aggregate(
            Base(target),
            (depth, operand) => Deepest(depth, Depth(operand, target))),
        OrNode or => or.Operands.Aggregate(
            Base(target),
            (depth, operand) => Deepest(depth, Depth(operand, target))),
        NotNode not => Depth(not.Operand, target),
        ComparisonNode comparison => Deepest(
            Depth(comparison.Left, target),
            Depth(comparison.Right, target)),
        StringNode text => Deepest(Depth(text.Left, target), Depth(text.Right, target)),
        RegexNode regex => Depth(regex.Input, target),
        FieldNode field when QueryFieldCatalog.IsRelation(field.WireName) =>
            RelationDepth(field.WireName),
        FieldNode field => Base(field.Target),
        _ => Base(target),
    };

    private static SnapshotDepth RelationDepth(string wireName) => wireName switch
    {
        "session_windows" => SnapshotDepth.Windows,
        "window_panes" => SnapshotDepth.Panes,
        _ => SnapshotDepth.Sessions,
    };

    private static SnapshotDepth Base(QueryTarget target) => target switch
    {
        QueryTarget.Window => SnapshotDepth.Windows,
        QueryTarget.Pane => SnapshotDepth.Panes,
        _ => SnapshotDepth.Sessions,
    };

    private static SnapshotDepth Deepest(SnapshotDepth left, SnapshotDepth right) =>
        left > right ? left : right;
}
