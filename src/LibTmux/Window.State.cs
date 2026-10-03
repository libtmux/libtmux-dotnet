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

    /// <summary>Gets whether this is the current window of the session it was read through.</summary>
    /// <remarks>
    /// A window linked into several sessions can be current in one and not in
    /// another, as with <see cref="Index" />.
    /// </remarks>
    public bool Active => ReadSnapshot("window_active") == "1";

    /// <summary>Gets whether a bell rang in the window since it was last the current window.</summary>
    /// <remarks>tmux sets it only while <c>monitor-bell</c> is on, which it is by default.</remarks>
    public bool BellAlert => ReadSnapshot("window_bell_flag") == "1";

    /// <summary>Gets whether the window printed since it was last the current window.</summary>
    /// <remarks>tmux sets it only while <c>monitor-activity</c> is on.</remarks>
    public bool ActivityAlert => ReadSnapshot("window_activity_flag") == "1";

    /// <summary>Gets whether the window has been silent for <c>monitor-silence</c> seconds.</summary>
    public bool SilenceAlert => ReadSnapshot("window_silence_flag") == "1";

    /// <summary>Gets whether one of the window's panes is zoomed to fill it.</summary>
    public bool Zoomed => ReadSnapshot("window_zoomed_flag") == "1";

    /// <summary>Gets the window's flags as its status line shows them, such as <c>*</c> for the current window.</summary>
    /// <remarks>Empty for a window with no flags.</remarks>
    /// <exception cref="IncompleteSnapshotException">The flags were not captured.</exception>
    public string Flags =>
        _snapshot is not null && _snapshot.TryGetValue("window_flags", out string? flags)
            ? flags ?? string.Empty
            : throw new IncompleteSnapshotException("flags", SnapshotDepth.Windows);

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
