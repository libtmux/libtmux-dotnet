using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides pane hierarchy relations captured with the pane.
public sealed partial class Pane
{
    private readonly Server? _owner;
    private Window? _capturedWindow;

    /// <summary>Gets the server that owns this pane.</summary>
    /// <remarks>
    /// Reading this uses the owner captured with the entity.
    /// </remarks>
    public Server Server => RequireOwner("server");

    /// <summary>Gets the session containing this pane.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane carries no captured session identity.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Session Session =>
        _capturedWindow?.Session
        ?? (SessionId.TryParse(ReadSnapshot("session_id"), out _)
            ? RelationReader.ToSession(Server, RawFormatFields)
            : throw new IncompleteSnapshotException("session", SnapshotDepth.Server));

    /// <summary>Gets the window containing this pane, with captured scalar state.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane carries no captured window identity.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Window Window =>
        _capturedWindow
        ?? (WindowId.TryParse(ReadSnapshot("window_id"), out _)
            ? RelationReader.ToWindow(Server, RawFormatFields)
            : throw new IncompleteSnapshotException("window", SnapshotDepth.Server));

    internal void WithCaptured(Window window) => _capturedWindow = window;

    private Server RequireOwner(string relation) =>
        _owner ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
