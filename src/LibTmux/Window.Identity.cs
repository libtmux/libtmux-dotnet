using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides typed window identity.
public sealed partial class Window : IEquatable<Window>
{
    private readonly WindowId _id;
    private readonly ServerGeneration _generation;
    private readonly IReadOnlyDictionary<string, string?>? _snapshot;

    [UnsupportedOSPlatform("windows")]
    internal Window(
        Server owner,
        TmuxConnection connection,
        ServerGeneration generation,
        WindowId id)
        : this(connection.CreateEntityDispatcher(generation), TmuxTarget.From(id).Value)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _id = id;
        _generation = generation;
    }

    [UnsupportedOSPlatform("windows")]
    internal Window(
        Server owner,
        TmuxConnection connection,
        ServerGeneration generation,
        WindowId id,
        IReadOnlyDictionary<string, string?> snapshot)
        : this(owner, connection, generation, id)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    /// <summary>Gets the tmux fields captured when this handle materialized, or null when none were.</summary>
    /// <remarks>
    /// A handle resolved by identifier alone carries no snapshot, so callers
    /// must ask whether one was captured rather than read empty fields.
    /// </remarks>
    internal IReadOnlyDictionary<string, string?>? Snapshot => _snapshot;

    /// <summary>Gets the tmux fields captured when this handle materialized.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public IReadOnlyDictionary<string, string?> RawFormatFields =>
        _snapshot ?? throw new IncompleteSnapshotException("format fields", SnapshotDepth.Windows);

    /// <summary>Gets the window identifier.</summary>
    public WindowId Id => _id;

    /// <summary>Gets the server generation captured with this window.</summary>
    public ServerGeneration Generation => _generation;

    /// <summary>Reports whether two handles name the same window.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when both name the same window on the same server generation.</returns>
    public static bool operator ==(Window? left, Window? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Reports whether two handles name different windows.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when they do not name the same window.</returns>
    public static bool operator !=(Window? left, Window? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Window? other) =>
        other is not null && _generation == other._generation && _id == other._id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Window);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_generation, _id);
}
