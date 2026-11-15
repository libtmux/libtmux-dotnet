namespace LibTmux;

/// <summary>Represents an immutable window handle and snapshot.</summary>
/// <remarks>
/// <para>
/// Thread-safe: the handle is immutable once constructed and may be shared
/// freely, including as a singleton. Every mutating call answers a new handle
/// rather than changing this one.
/// </para>
/// <para>
/// <b>What is on here</b>
/// </para>
/// <para>
/// A tmux window holds panes and has a good many members of its own. They fall
/// into a few groups:
/// </para>
/// <para>
/// <b>Reaching a window.</b> Ask for the window holding the pane this process
/// runs in. It is resolved through the pane rather than the session id in
/// <c>TMUX</c>, which is frozen at pane spawn and goes stale once the window is
/// moved.
/// </para>
/// <para>
/// <see cref="Window.FromEnvironmentAsync" />
/// </para>
/// <para>
/// <b>Identity and state.</b> A handle carries what tmux reported when it was
/// made, so reading its name, index, size, or flags is local and never reaches
/// tmux; re-read the window to see current state. Two handles are equal when
/// they name the same window on the same server generation, so two placements
/// of one linked window are equal. A window linked into several sessions holds
/// a different index in each, and the index and the active flag describe the
/// session this handle was read through.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Identity: <see cref="Window.Id" />,
/// <see cref="Window.Name" />,
/// <see cref="Window.Index" />,
/// <see cref="Window.Generation" /></description>
/// </item>
/// <item>
/// <description>State: <see cref="Window.Active" />,
/// <see cref="Window.Zoomed" />,
/// <see cref="Window.Flags" />,
/// <see cref="Window.Layout" />,
/// <see cref="Window.Width" />,
/// <see cref="Window.Height" /></description>
/// </item>
/// <item>
/// <description>Alerts: <see cref="Window.BellAlert" />,
/// <see cref="Window.ActivityAlert" />,
/// <see cref="Window.SilenceAlert" /></description>
/// </item>
/// <item>
/// <description>Raw: <see cref="Window.RawFormatFields" /></description>
/// </item>
/// <item>
/// <description>Owner: <see cref="Window.Server" />,
/// <see cref="Window.Session" /></description>
/// </item>
/// <item>
/// <description>Re-read: <see cref="Window.RefreshAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Looking inside.</b> Read this window's panes from tmux, or hand tmux an
/// expression to keep only some of them. The captured relations hold what a
/// capture found instead: reading them never reaches tmux, and one the capture
/// did not read stays uncaptured. Reading the active pane from tmux gets the
/// pane the window shows now, and throws
/// <see cref="TmuxObjectNotFoundException" /> when tmux reports none. A window can
/// be linked into several sessions, so reading its sessions reports every one
/// rather than a single parent.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Read: <see cref="Window.GetPanesAsync" />,
/// <see cref="Window.GetActivePaneAsync" />,
/// <see cref="Window.GetLinkedSessionsAsync" /></description>
/// </item>
/// <item>
/// <description>Search: <see cref="Window.SearchPanesAsync" /></description>
/// </item>
/// <item>
/// <description>Captured: <see cref="Window.Panes" />,
/// <see cref="Window.ActivePane" />,
/// <see cref="Window.LinkedSessions" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Finding a pane.</b> Read one pane of this window by a string target. The
/// Get method throws <see cref="TmuxObjectNotFoundException" /> when this
/// window has no such pane; the Find method answers <c>null</c>.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Get: <see cref="Window.GetPaneAsync" /></description>
/// </item>
/// <item>
/// <description>Find: <see cref="Window.FindPaneAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Splitting and arranging.</b> Splitting makes a tiled pane and creating a
/// pane makes a floating one, which needs tmux 3.7. A layout is applied by
/// request or stepped to the next or previous one. Feeding a captured layout
/// back restores the same pane sizes, but on tmux 3.7 and earlier it can rotate
/// which pane lands in which position, where tmux 3.8 and newer accepts the
/// JSON form and restores the exact arrangement. Resizing switches the window's
/// <c>window-size</c> option to manual, so it stops following its clients.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Panes: <see cref="Window.SplitPaneAsync" />,
/// <see cref="Window.CreatePaneAsync" /></description>
/// </item>
/// <item>
/// <description>Layout: <see cref="Window.SelectLayoutAsync" />,
/// <see cref="Window.SelectNextLayoutAsync" />,
/// <see cref="Window.SelectPreviousLayoutAsync" /></description>
/// </item>
/// <item>
/// <description>Arrange: <see cref="Window.RotateAsync" />,
/// <see cref="Window.ResizeAsync" /></description>
/// </item>
/// <item>
/// <description>Select a pane: <see cref="Window.SelectPaneAsync" />,
/// <see cref="Window.SelectLastPaneAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Moving and linking.</b> Linking puts the window into another session,
/// unlinking removes only the placement it was read through, and moving takes
/// it to another index or session. tmux refuses to unlink a window that belongs
/// to only one session unless the call lets the window die. A guard in the
/// native queue checks that the captured placement still names this window
/// before it links, unlinks, or moves, and a swap on a window linked into
/// several sessions resolves to whichever link tmux picks.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Link: <see cref="Window.LinkAsync" />,
/// <see cref="Window.UnlinkAsync(bool, CancellationToken)" />,
/// <see cref="Window.UnlinkAsync(bool, IReadOnlyList{PaneId},
/// CancellationToken)" /></description>
/// </item>
/// <item>
/// <description>Move: <see cref="Window.MoveAsync" />,
/// <see cref="Window.SwapAsync" /></description>
/// </item>
/// <item>
/// <description>Beside this one: <see cref="Window.CreateWindowAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Changing the window.</b> Renaming answers a replacement handle carrying
/// the new name, and tmux expands the name as a format, so a <c>#</c> in it
/// does not survive verbatim. Selecting makes the window current in its
/// session. Respawning restarts the window's command, and tmux refuses it while
/// the window is still running unless the request kills it first, which
/// destroys every pane but one. Stopping a window can stop every other window
/// in the session instead.
/// </para>
/// <para>
/// <see cref="Window.RenameAsync" />,
/// <see cref="Window.SelectAsync" />,
/// <see cref="Window.RespawnAsync" />,
/// <see cref="Window.KillAsync" />
/// </para>
/// <para>
/// <b>Messages.</b> Show a message on the client viewing this window. A message
/// with no client to show it on is not a failure, so tmux's complaint is logged
/// rather than raised.
/// </para>
/// <para>
/// <see cref="Window.DisplayMessageAsync" />
/// </para>
/// <para>
/// <b>Options and hooks.</b> The window's options and its hooks are objects of
/// their own: <see cref="TmuxOptions" /> and <see cref="TmuxHooks" />.
/// </para>
/// <para>
/// <see cref="Window.Options" />,
/// <see cref="Window.Hooks" />
/// </para>
/// <para>
/// Anything tmux can do to a window that is not here is reachable through
/// <see cref="Window.ExecuteCommandAsync" />, which runs one raw command
/// against this window.
/// </para>
/// </remarks>
public sealed partial class Window
{
}
