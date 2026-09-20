using System.Globalization;
using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

// Provides captured window state and refresh.
public sealed partial class Window
{
    /// <summary>Gets the window name captured with this handle.</summary>
    public string Name =>
        ReadSnapshot("window_name")
        ?? throw new IncompleteSnapshotException("name", SnapshotDepth.Windows);

    /// <summary>Gets the index this window holds in its session.</summary>
    /// <remarks>
    /// A window linked into several sessions holds a different index in each,
    /// so this is the index of the session this handle was read through.
    /// </remarks>
    public int Index => ReadCapturedInt("window_index", "index");

    /// <summary>Gets whether this captured placement is the selected window in its session.</summary>
    /// <exception cref="IncompleteSnapshotException">The active flag was not captured.</exception>
    /// <exception cref="TmuxProtocolException">The captured flag is neither zero nor one.</exception>
    public bool IsActive => ReadSnapshot("window_active") switch
    {
        "1" => true,
        "0" => false,
        null => throw new IncompleteSnapshotException("active window placement", SnapshotDepth.Windows),
        string value => throw new TmuxProtocolException(
            $"Captured window_active value '{value}' is not zero or one.",
            value,
            TmuxDispatchState.NotDispatched),
    };

    /// <summary>Gets the window height captured with this handle.</summary>
    public int Height => ReadCapturedInt("window_height", "height");

    /// <summary>Gets the window width captured with this handle.</summary>
    public int Width => ReadCapturedInt("window_width", "width");

    /// <summary>Gets the layout string captured with this handle.</summary>
    /// <remarks>
    /// Restoring this through <see cref="SelectLayoutAsync" /> can rotate
    /// which pane lands in which position; see that method's remarks for when.
    /// </remarks>
    public string Layout =>
        ReadSnapshot("window_layout")
        ?? throw new IncompleteSnapshotException("layout", SnapshotDepth.Windows);

    /// <summary>Gets the server that owns this window.</summary>
    /// <remarks>
    /// Reading this uses the owner captured with the entity.
    /// </remarks>
    public Server Server => RequireOwner("server");

    /// <summary>Gets the session this window was read through.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window carries no captured session identity.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Session Session =>
        _capturedSession
        ?? (SessionId.TryParse(ReadSnapshot("session_id"), out _)
            ? RelationReader.ToSession(RequireOwner("session"), RawFormatFields)
            : throw new IncompleteSnapshotException("session", SnapshotDepth.Windows));

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
                ScopedTarget(),
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
