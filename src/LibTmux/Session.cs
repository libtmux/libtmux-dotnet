namespace LibTmux;

/// <summary>Represents an immutable session handle and snapshot.</summary>
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
/// A tmux session holds windows and has a good many members of its own. They
/// fall into a few groups:
/// </para>
/// <para>
/// <b>Reaching a session.</b> Read one from the server by identifier, or ask
/// for the session holding the pane this process runs in. That one is resolved
/// through the pane rather than the session id in <c>TMUX</c>, which is frozen
/// at pane spawn and names the wrong session once the pane's window is moved or
/// linked elsewhere.
/// </para>
/// <para>
/// <see cref="Server.GetSessionAsync" />,
/// <see cref="Server.FindSessionAsync" />,
/// <see cref="Session.FromEnvironmentAsync" />
/// </para>
/// <para>
/// <b>Identity and state.</b> A handle carries what tmux reported when it was
/// made, so reading its name, attachment, or fields is local and never reaches
/// tmux; re-read the session to see current state. Two handles are equal when
/// they name the same session on the same server generation. The control client
/// this library keeps on the server does not count as attached.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Identity: <see cref="Session.Id" />,
/// <see cref="Session.Name" />, <see cref="Session.Generation" /></description>
/// </item>
/// <item>
/// <description>State: <see cref="Session.Attached" />,
/// <see cref="Session.RawFormatFields" /></description>
/// </item>
/// <item>
/// <description>Owner: <see cref="Session.Server" /></description>
/// </item>
/// <item>
/// <description>Re-read: <see cref="Session.RefreshAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Looking inside.</b> Read this session's windows or panes from tmux, or
/// hand tmux an <see cref="UnsafeTmuxFilter" /> to keep only some of them. The
/// captured relations hold what a capture found instead: reading them never
/// reaches tmux, and one the capture did not read stays uncaptured. Reading the
/// active pane from tmux gets the pane the session shows now, and throws
/// <see cref="TmuxObjectNotFoundException" /> when tmux reports none.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Read: <see cref="Session.GetWindowsAsync" />,
/// <see cref="Session.GetPanesAsync" />,
/// <see cref="Session.GetActivePaneAsync" /></description>
/// </item>
/// <item>
/// <description>Search: <see cref="Session.SearchWindowsAsync" />,
/// <see cref="Session.SearchPanesAsync" /></description>
/// </item>
/// <item>
/// <description>Captured: <see cref="Session.Windows" />,
/// <see cref="Session.Panes" /></description>
/// </item>
/// <item>
/// <description>Active: <see cref="Session.ActiveWindow" />,
/// <see cref="Session.ActivePane" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Finding a window.</b> Read one window of this session, by typed
/// identifier or by a string target. The Get methods throw
/// <see cref="TmuxObjectNotFoundException" /> when this session has no such
/// window; the Find methods answer <c>null</c>, including for a target that
/// names a window in another session. A string target also accepts a window
/// name, which two windows can share.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Get by identifier:
/// <see cref="Session.GetWindowAsync(WindowId, CancellationToken)" />
/// </description>
/// </item>
/// <item>
/// <description>Get by target:
/// <see cref="Session.GetWindowAsync(string, CancellationToken)" />
/// </description>
/// </item>
/// <item>
/// <description>Find by identifier:
/// <see cref="Session.FindWindowAsync(WindowId, CancellationToken)" />
/// </description>
/// </item>
/// <item>
/// <description>Find by target:
/// <see cref="Session.FindWindowAsync(string, CancellationToken)" />
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Windows.</b> Create a window in this session, or create one inside a
/// scope that stops it when disposed. The receipt form also returns the first
/// pane's identifier from the same creation reply, and throws
/// <see cref="ArgumentException" /> when the request may select an existing
/// window, since an existing window has no creation receipt. Selecting a
/// window, or stepping to the next, previous, or last one, answers the window
/// that is active afterwards; stopping a window with no target stops the active
/// one.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Create: <see cref="Session.CreateWindowAsync" />,
/// <see cref="Session.CreateWindowWithReceiptAsync" />,
/// <see cref="Session.CreateOwnedWindowAsync" /></description>
/// </item>
/// <item>
/// <description>Select: <see cref="Session.SelectWindowAsync" /></description>
/// </item>
/// <item>
/// <description>Step: <see cref="Session.SelectNextWindowAsync" />,
/// <see cref="Session.SelectPreviousWindowAsync" />,
/// <see cref="Session.SelectLastWindowAsync" /></description>
/// </item>
/// <item>
/// <description>Stop: <see cref="Session.KillWindowAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Changing the session.</b> Renaming answers a replacement handle carrying
/// the new name, and tmux expands the name as a format, so a <c>#</c> in it
/// does not survive verbatim. Stopping a session can stop every other session
/// instead, or its whole group. Group stopping needs tmux 3.7: older servers
/// reject the flag, so against them the request is logged and the flag omitted,
/// and this session stops while its group siblings do not.
/// </para>
/// <para>
/// <see cref="Session.RenameAsync" />, <see cref="Session.KillAsync" />,
/// <see cref="Session.LockAsync" />
/// </para>
/// <para>
/// <b>Clients.</b> Attaching inherits the caller's terminal on stdin and waits
/// for detach, and needs a positive acknowledgement within five seconds.
/// Switching moves the current client to this session, and detaching detaches
/// every client attached to it. Holding the control client that waits on this
/// session's panes saves an attach for each wait in a series, while tmux still
/// lists the held client among the session's clients.
/// </para>
/// <para>
/// <see cref="Session.AttachAsync" />,
/// <see cref="Session.SwitchClientAsync" />,
/// <see cref="Session.DetachClientAsync" />,
/// <see cref="Session.HoldWaitClientAsync" />
/// </para>
/// <para>
/// <b>Options and hooks.</b> The session's options, its hooks, and the
/// environment panes created in it inherit are objects of their own:
/// <see cref="TmuxOptions" />, <see cref="TmuxHooks" />, and
/// <see cref="TmuxEnvironment" />.
/// </para>
/// <para>
/// <see cref="Session.Options" />, <see cref="Session.Hooks" />,
/// <see cref="Session.Environment" />
/// </para>
/// <para>
/// Anything tmux can do to a session that is not here is reachable through
/// <see cref="Session.ExecuteCommandAsync" />, which runs one raw command
/// against this session.
/// </para>
/// </remarks>
public sealed partial class Session
{
}
