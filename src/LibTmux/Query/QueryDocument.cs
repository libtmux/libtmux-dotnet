namespace LibTmux.Query;

/// <summary>One translated query predicate and its wire schema.</summary>
/// <remarks>
/// The document is the stable interchange form. It is produced by translation
/// and never by client evaluation, so an expression that cannot be translated
/// fails loudly rather than silently degrading to in-memory filtering.
/// </remarks>
public sealed record QueryDocument
{
    internal QueryDocument(string schema, int version, QueryTarget target, QueryNode predicate)
    {
        Schema = schema;
        Version = version;
        Target = target;
        Predicate = predicate;
    }

    /// <summary>Gets the wire schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Gets the wire schema version.</summary>
    public int Version { get; }

    /// <summary>Gets the object the predicate selects.</summary>
    public QueryTarget Target { get; }

    /// <summary>Gets the translated predicate.</summary>
    /// <remarks>
    /// The shape of a predicate is this library's to change. A caller reads a
    /// document through <see cref="QueryExtensions" /> or moves it as JSON;
    /// nothing outside needs its nodes, and exporting them would freeze the
    /// translator's internals as a promise.
    /// </remarks>
    internal QueryNode Predicate { get; }

    /// <summary>The current wire schema identifier.</summary>
    public const string CurrentSchema = "libtmux-query";

    /// <summary>The supported wire schema version.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Gets the snapshot depth this predicate needs to evaluate.</summary>
    /// <remarks>
    /// A quantifier over a relation cannot be answered by a shallower capture,
    /// so the depth is derived from the predicate rather than assumed.
    /// </remarks>
    /// <exception cref="UnsupportedQueryExpressionException">
    /// The predicate is malformed or exceeds the structural limits.
    /// </exception>
    public SnapshotDepth RequiredSnapshotDepth
    {
        get
        {
            _ = QueryDocumentValidator.Validate(this);
            return Depth(Predicate, Target);
        }
    }

    private static SnapshotDepth Depth(QueryNode node, QueryTarget target) => node switch
    {
        QuantifierNode quantifier => Deepest(
            RelationDepth(quantifier.Relation.WireName),
            Depth(quantifier.Predicate, target)),
        RelatedNode related => Deepest(
            RelationDepth(related.Relation.WireName),
            Depth(related.Predicate, target)),
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

    private static SnapshotDepth RelationDepth(string wireName) =>
        QueryFieldCatalog.TryGetRelation(wireName, out QueryRelationDefinition relation)
            ? relation.Depth
            : throw new UnsupportedQueryExpressionException($"Field '{wireName}' is not a relation.");

    private static SnapshotDepth Base(QueryTarget target) => target switch
    {
        QueryTarget.Window => SnapshotDepth.Windows,
        QueryTarget.Pane => SnapshotDepth.Panes,
        _ => SnapshotDepth.Sessions,
    };

    private static SnapshotDepth Deepest(SnapshotDepth left, SnapshotDepth right) =>
        left > right ? left : right;
}
