using System.Collections.Frozen;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides typed pane identity.
public sealed partial class Pane : IEquatable<Pane>
{
    private readonly PaneId _id;
    private readonly ServerGeneration _generation;
    private readonly FrozenDictionary<string, string?>? _snapshot;

    [UnsupportedOSPlatform("windows")]
    internal Pane(
        Server owner,
        TmuxConnection connection,
        ServerGeneration generation,
        PaneId id,
        IReadOnlyDictionary<string, string?> snapshot)
        : this(connection.CreateEntityDispatcher(generation), TmuxTarget.From(id).Value)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(snapshot);
        _owner = owner;
        _id = id;
        _generation = generation;
        _snapshot = snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal IReadOnlyDictionary<string, string?>? Snapshot => _snapshot;

    /// <summary>Gets the tmux fields captured when this handle materialized.</summary>
    public IReadOnlyDictionary<string, string?> RawFormatFields =>
        _snapshot ?? throw new IncompleteSnapshotException("format fields", SnapshotDepth.Panes);

    /// <summary>Gets the pane identifier.</summary>
    public PaneId Id => _id;

    /// <summary>Gets the server generation captured with this pane.</summary>
    public ServerGeneration Generation => _generation;

    /// <summary>Reports whether two handles name the same pane.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when both name the same pane on the same server generation.</returns>
    public static bool operator ==(Pane? left, Pane? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Reports whether two handles name different panes.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when they do not name the same pane.</returns>
    public static bool operator !=(Pane? left, Pane? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Pane? other) =>
        other is not null && _generation == other._generation && _id == other._id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Pane);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_generation, _id);
}
