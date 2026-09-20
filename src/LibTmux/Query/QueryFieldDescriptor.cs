namespace LibTmux.Query;

/// <summary>Describes one field accepted by the current portable query schema.</summary>
/// <remarks>
/// Property paths describe built-in entity bindings. A schema-only field has no
/// property path. Uncaptured state still throws; it is not a nullable field value.
/// </remarks>
public sealed class QueryFieldDescriptor
{
    internal QueryFieldDescriptor(
        string wireName,
        QueryTarget target,
        QueryValueKind? valueKind,
        string? scalarPropertyPath,
        string? relationPropertyPath,
        QueryTarget? relatedTarget,
        QueryRelationCardinality? cardinality,
        SnapshotDepth? minimumSnapshotDepth,
        bool? isNullable,
        IReadOnlyList<string> operators)
    {
        WireName = wireName;
        Target = target;
        ValueKind = valueKind;
        ScalarPropertyPath = scalarPropertyPath;
        RelationPropertyPath = relationPropertyPath;
        RelatedTarget = relatedTarget;
        Cardinality = cardinality;
        MinimumSnapshotDepth = minimumSnapshotDepth;
        IsNullable = isNullable;
        Operators = Array.AsReadOnly(operators.ToArray());
    }

    /// <summary>Gets the field name used in query JSON.</summary>
    public string WireName { get; }

    /// <summary>Gets the target that owns this field.</summary>
    public QueryTarget Target { get; }

    /// <summary>Gets the scalar type, or null for a to-one relation.</summary>
    public QueryValueKind? ValueKind { get; }

    /// <summary>Gets the native scalar property path, or null without a scalar binding.</summary>
    /// <remarks>Collection counts end in <c>.Count</c>.</remarks>
    public string? ScalarPropertyPath { get; }

    /// <summary>Gets the native relation property path, or null without a relation binding.</summary>
    /// <remarks>Captured active values end in <c>.Value</c>.</remarks>
    public string? RelationPropertyPath { get; }

    /// <summary>Gets the related target kind, or null for a scalar-only field.</summary>
    public QueryTarget? RelatedTarget { get; }

    /// <summary>Gets the relation cardinality, or null for a scalar-only field.</summary>
    public QueryRelationCardinality? Cardinality { get; }

    /// <summary>Gets the field's minimum hierarchy capture depth, or null outside that hierarchy.</summary>
    /// <remarks>
    /// Clients and schema-only fields have no hierarchy capture depth. Nested
    /// predicates may need a deeper capture; read <see cref="QueryDocument.RequiredSnapshotDepth" />.
    /// </remarks>
    public SnapshotDepth? MinimumSnapshotDepth { get; }

    /// <summary>Gets whether the captured native scalar can be null, or null without a scalar binding.</summary>
    /// <remarks>The schema permits null equality comparisons even for nonnullable fields.</remarks>
    public bool? IsNullable { get; }

    /// <summary>Gets the accepted wire operation names for this field.</summary>
    /// <remarks>
    /// Comparison names populate <c>operator</c>; <c>any</c> and <c>all</c> populate
    /// <c>quantifier</c>. <c>regex</c> and <c>related</c> identify their node kinds.
    /// Boolean composition is independent of field operations.
    /// </remarks>
    public IReadOnlyList<string> Operators { get; }
}
