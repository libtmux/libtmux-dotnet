using System.Globalization;
using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

// Provides captured window state and refresh.
public sealed partial class Window
{
    /// <summary>Gets the window name captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public string Name =>
        ReadSnapshot("window_name")
        ?? throw new IncompleteSnapshotException("name", SnapshotDepth.Windows);

    /// <summary>Gets the index this window holds in its session.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    /// <remarks>
    /// A window linked into several sessions holds a different index in each,
    /// so this is the index of the session this handle was read through.
    /// </remarks>
    public int Index => ReadCapturedInt("window_index", "index");

    /// <summary>Gets the window height captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public int Height => ReadCapturedInt("window_height", "height");

    /// <summary>Gets the window width captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public int Width => ReadCapturedInt("window_width", "width");

    /// <summary>Gets the server that owns this window.</summary>
    /// <remarks>
    /// Every handle reached through a server carries it, whether the handle was
    /// materialized from a listing or resolved from an identifier.
    /// </remarks>
    public Server Server => RequireOwner("server");

    /// <summary>Gets the session this window was read through.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Session Session =>
        SessionId.TryParse(ReadSnapshot("session_id"), out SessionId id)
            ? new Session(RequireOwner("session"), RequireConnection(), _generation, id)
            : throw new IncompleteSnapshotException("session", SnapshotDepth.Windows);

    /// <summary>Re-reads this window from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("refresh");
        IReadOnlyDictionary<string, string?> row = await RelationReader
            .FindAsync(
                owner,
                "list-windows",
                "window_id",
                _id.ToString(),
                RelationReader.CapturedSession(_snapshot) is SessionId session
                    ? TmuxTarget.In(session, _id)
                    : null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has window '{_id}'.",
                _id.ToString());
        return RelationReader.ToWindow(owner, row);
    }

    private int ReadCapturedInt(string wireName, string relation) =>
        int.TryParse(
            ReadSnapshot(wireName),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : throw new IncompleteSnapshotException(relation, SnapshotDepth.Windows);
}
