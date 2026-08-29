using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides pane hierarchy relations captured with the pane.
public sealed partial class Pane
{
    private readonly Server? _owner;

    /// <summary>Gets the server that owns this pane.</summary>
    /// <remarks>
    /// Every handle reached through a server carries it, whether the handle was
    /// materialized from a listing or resolved from an identifier.
    /// </remarks>
    public Server Server =>
        _owner ?? throw new IncompleteSnapshotException("server", SnapshotDepth.Server);

    /// <summary>Gets the session containing this pane.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane carries no captured session identity.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Session Session
    {
        get
        {
            if (!SessionId.TryParse(ReadSnapshot("session_id"), out SessionId id))
            {
                throw new IncompleteSnapshotException("session", SnapshotDepth.Server);
            }

            return new Session(Server, RequireConnection(), _generation, id);
        }
    }

    /// <summary>Gets the window containing this pane.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane carries no captured window identity.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Window Window
    {
        get
        {
            if (!WindowId.TryParse(ReadSnapshot("window_id"), out WindowId id))
            {
                throw new IncompleteSnapshotException("window", SnapshotDepth.Server);
            }

            return new Window(Server, RequireConnection(), _generation, id);
        }
    }

    private TmuxConnection RequireConnection() =>
        Server.Connection
        ?? throw new IncompleteSnapshotException("connection", SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
