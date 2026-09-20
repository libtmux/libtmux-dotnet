using System.Diagnostics.CodeAnalysis;

namespace LibTmux;

/// <summary>Holds the one child a snapshot captured for a relation, if it read it.</summary>
/// <typeparam name="T">The captured child type.</typeparam>
/// <remarks>
/// A relation that holds at most one child says so in its type. An uncaptured
/// value is not an absent one: reading <see cref="Value" /> when the snapshot
/// never looked throws <see cref="IncompleteSnapshotException" /> rather than
/// answering null, so a caller cannot mistake unread state for absent state.
/// </remarks>
public sealed class CapturedValue<T>
    where T : class
{
    private readonly T? _value;

    internal CapturedValue(T? value, string relation, SnapshotDepth capturedDepth)
    {
        _value = value;
        Relation = relation;
        CapturedDepth = capturedDepth;
    }

    /// <summary>Gets the relation name this instance carries.</summary>
    public string Relation { get; }

    /// <summary>Gets the depth the owning snapshot reached.</summary>
    public SnapshotDepth CapturedDepth { get; }

    /// <summary>Gets whether the snapshot read this relation.</summary>
    public bool IsCaptured => _value is not null;

    /// <summary>Gets the captured child.</summary>
    /// <exception cref="IncompleteSnapshotException">The snapshot never read it.</exception>
    public T Value =>
        _value ?? throw new IncompleteSnapshotException(Relation, CapturedDepth);

    /// <summary>Gets the captured child, or null when the snapshot never read it.</summary>
    /// <returns>The child, or null.</returns>
    public T? OrNull() => _value;

    /// <summary>Tries to read the captured child.</summary>
    /// <param name="value">The child when the snapshot read it.</param>
    /// <returns><see langword="true" /> when the snapshot read it.</returns>
    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = _value;
        return value is not null;
    }
}

internal static class CapturedValue
{
    internal static CapturedValue<T> Capture<T>(
        T value,
        string relation,
        SnapshotDepth capturedDepth)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        return new CapturedValue<T>(value, relation, capturedDepth);
    }

    internal static CapturedValue<T> Uncaptured<T>(string relation, SnapshotDepth capturedDepth)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        return new CapturedValue<T>(null, relation, capturedDepth);
    }
}
