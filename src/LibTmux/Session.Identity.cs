using System.Collections.Frozen;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides typed session identity.
public sealed partial class Session : IEquatable<Session>
{
    private readonly SessionId _id;
    private readonly ServerGeneration _generation;
    private readonly FrozenDictionary<string, string?>? _snapshot;

    [UnsupportedOSPlatform("windows")]
    internal Session(
        Server owner,
        TmuxConnection connection,
        ServerGeneration generation,
        SessionId id,
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
        _snapshot ?? throw new IncompleteSnapshotException("format fields", SnapshotDepth.Sessions);

    /// <summary>Gets the session identifier.</summary>
    public SessionId Id => _id;

    /// <summary>Gets the server generation captured with this session.</summary>
    public ServerGeneration Generation => _generation;

    /// <summary>Reports whether two handles name the same session.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when both name the same session on the same server generation.</returns>
    public static bool operator ==(Session? left, Session? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Reports whether two handles name different sessions.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when they do not name the same session.</returns>
    public static bool operator !=(Session? left, Session? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Session? other) =>
        other is not null && _generation == other._generation && _id == other._id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Session);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_generation, _id);
}
