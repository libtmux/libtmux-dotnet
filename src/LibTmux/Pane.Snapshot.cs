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

    /// <summary>Gets the pane height captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Height => ReadCapturedInt("pane_height", "height");

    /// <summary>Gets the pane width captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Width => ReadCapturedInt("pane_width", "width");

    /// <summary>Gets the index this pane holds in its window.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Index => ReadCapturedInt("pane_index", "index");

    /// <summary>Gets the pane title captured with this handle.</summary>
    public string? Title => ReadSnapshot("pane_title");

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
