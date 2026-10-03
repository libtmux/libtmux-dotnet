using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Pane
{
    /// <summary>Gets whether the pane touches the top of its window.</summary>
    public bool AtTop => ReadSnapshot("pane_at_top") == "1";

    /// <summary>Gets whether the pane touches the bottom of its window.</summary>
    public bool AtBottom => ReadSnapshot("pane_at_bottom") == "1";

    /// <summary>Gets whether the pane touches the left of its window.</summary>
    public bool AtLeft => ReadSnapshot("pane_at_left") == "1";

    /// <summary>Gets whether the pane touches the right of its window.</summary>
    public bool AtRight => ReadSnapshot("pane_at_right") == "1";

    /// <summary>Gets whether the pane is its window's active pane.</summary>
    public bool Active => ReadSnapshot("pane_active") == "1";

    /// <summary>Gets whether the pane's program has exited and the pane remains.</summary>
    /// <remarks>A pane outlives its program only while <c>remain-on-exit</c> is on.</remarks>
    public bool Dead => ReadSnapshot("pane_dead") == "1";

    /// <summary>Gets whether keys typed into the pane go to every synchronized pane in its window.</summary>
    public bool Synchronized => ReadSnapshot("pane_synchronized") == "1";

    /// <summary>Gets whether the pane is in a mode, such as copy mode.</summary>
    /// <remarks>tmux reports how many modes are stacked on the pane; any number but zero is true.</remarks>
    public bool InMode => ReadSnapshot("pane_in_mode") is not (null or "" or "0");

    /// <summary>Gets the process ID of the program the pane started.</summary>
    /// <remarks>A dead pane keeps the ID of the program that exited.</remarks>
    public int ProcessId => ReadCapturedInt("pane_pid", "process ID");

    /// <summary>Gets the pane height captured with this handle.</summary>
    public int Height => ReadCapturedInt("pane_height", "height");

    /// <summary>Gets the pane width captured with this handle.</summary>
    public int Width => ReadCapturedInt("pane_width", "width");

    /// <summary>Gets the pane's left offset, in cells, from its window's edge.</summary>
    public int Left => ReadCapturedInt("pane_left", "left");

    /// <summary>Gets the pane's top offset, in cells, from its window's edge.</summary>
    public int Top => ReadCapturedInt("pane_top", "top");

    /// <summary>Gets the index this pane holds in its window.</summary>
    public int Index => ReadCapturedInt("pane_index", "index");

    /// <summary>Gets the pane title captured with this handle.</summary>
    public string? Title => ReadSnapshot("pane_title");

    /// <summary>Gets the foreground command captured with this pane.</summary>
    /// <remarks>Returns null for a captured unavailable value. Reading this never reaches tmux.</remarks>
    /// <exception cref="IncompleteSnapshotException">The command field was not captured.</exception>
    public string? CurrentCommand => ReadCapturedText("pane_current_command", "current command");

    /// <summary>Gets the current working directory captured with this pane.</summary>
    /// <remarks>Returns null for a captured unavailable value. Reading this never reaches tmux.</remarks>
    /// <exception cref="IncompleteSnapshotException">The path field was not captured.</exception>
    public string? CurrentPath => ReadCapturedText("pane_current_path", "current path");

    private string? ReadCapturedText(string wireName, string relation) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : throw new IncompleteSnapshotException(relation, SnapshotDepth.Panes);

    /// <summary>Re-reads this pane from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Server owner = Server;
        IReadOnlyDictionary<string, string?> row = await RelationReader
            .FindAsync(
                owner,
                "list-panes",
                "pane_id",
                _id.ToString(),
                RelationReader.CapturedSession(_snapshot) is SessionId session
                    ? TmuxTarget.In(session, _id)
                    : null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has pane '{_id}'.",
                _id.ToString());
        return RelationReader.ToPane(owner, row);
    }
}
