using System.Collections;

namespace LibTmux;

/// <summary>Holds the children a snapshot captured for one relation.</summary>
/// <typeparam name="T">The captured child type.</typeparam>
/// <remarks>
/// An uncaptured relation is not an empty one. Enumerating a relation the
/// snapshot never read throws instead of reporting zero children, so a caller
/// cannot mistake unread state for absent state.
/// </remarks>
public sealed class CapturedRelation<T> : IReadOnlyList<T>
{
    private static readonly T[] None = [];
    private readonly T[]? _items;

    internal CapturedRelation(T[]? items, string relation, SnapshotDepth capturedDepth)
    {
        _items = items;
        Relation = relation;
        CapturedDepth = capturedDepth;
    }

    /// <summary>Gets the relation name this instance carries.</summary>
    public string Relation { get; }

    /// <summary>Gets the depth the owning snapshot reached.</summary>
    public SnapshotDepth CapturedDepth { get; }

    /// <summary>Gets whether the snapshot read this relation.</summary>
    public bool IsCaptured => _items is not null;

    /// <inheritdoc />
    public int Count => Captured.Length;

    /// <inheritdoc />
    public T this[int index] => Captured[index];

    private T[] Captured =>
        _items ?? throw new IncompleteSnapshotException(Relation, CapturedDepth);

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Captured).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Returns the captured children, or an empty list when unread.</summary>
    /// <returns>The children, empty when the relation was never captured.</returns>
    public IReadOnlyList<T> OrEmpty() => _items ?? None;
}

internal static class CapturedRelation
{
    internal static CapturedRelation<T> Capture<T>(
        IEnumerable<T> items,
        string relation,
        SnapshotDepth capturedDepth)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        return new CapturedRelation<T>([.. items], relation, capturedDepth);
    }

    internal static CapturedRelation<T> Uncaptured<T>(
        string relation,
        SnapshotDepth capturedDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        return new CapturedRelation<T>(null, relation, capturedDepth);
    }
}
