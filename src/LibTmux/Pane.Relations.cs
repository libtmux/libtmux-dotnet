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
    [UnsupportedOSPlatform("windows")]
    public Session Session =>
        _capturedWindow?.Session ?? RelationReader.ToSession(Server, RawFormatFields);

    /// <summary>Gets the window containing this pane, with captured scalar state.</summary>
    [UnsupportedOSPlatform("windows")]
    public Window Window => _capturedWindow ?? RelationReader.ToWindow(Server, RawFormatFields);

    internal void WithCaptured(Window window) => _capturedWindow = window;

    private Server RequireOwner(string relation) =>
        _owner ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
