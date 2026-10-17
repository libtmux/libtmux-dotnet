namespace LibTmux;

/// <summary>Represents an immutable server handle and snapshot.</summary>
/// <remarks>
/// <para>
/// Thread-safe: the handle is immutable once constructed and may be shared
/// freely, including as a singleton. Every mutating call answers a new handle
/// rather than changing this one.
/// </para>
/// <para>
/// A handle names the server on one socket, chosen by its
/// <see cref="ServerConnectionOptions" />, rather than the process behind it,
/// so it can be opened before any server is listening. Two handles that reach
/// the same endpoint are equal (<see cref="Server.Equals(Server)" />) and hash
/// alike (<see cref="Server.GetHashCode" />). Letting go of a handle never
/// stops the server; disposing an <see cref="OwnedServerScope" /> does.
/// </para>
/// <para>
/// <b>What is on here</b>
/// </para>
/// <para>
/// A tmux server does a great many things, so this type has a great many
/// members. They fall into a few groups:
/// </para>
/// <para>
/// <b>Connecting. </b><see cref="Server.Open" /> makes a handle without running
/// tmux, and <see cref="Server.ConnectionOptions" /> holds the options it was
/// opened with.
/// <see cref="Server.ConnectAsync(ServerConnectionOptions, CancellationToken)" />
/// takes options and finds the server already listening, never starting one;
/// the instance <see cref="Server.ConnectAsync(CancellationToken)" /> does the
/// same for a handle from <c>Open</c>. <see cref="Server.FromEnvironment" />
/// reads the server this process is already inside, and
/// <see cref="Server.CreateOwnedAsync" /> starts a server and owns it, stopping
/// it when the scope is disposed. A connected handle is bound to the server it
/// found, so after tmux restarts its sessions, windows, and panes throw
/// <see cref="StaleServerGenerationException" /> rather than reach the new one.
/// <see cref="Server.IsMaterialized" /> says whether a handle has found a
/// server, <see cref="Server.Generation" /> which one, and
/// <see cref="Server.Version" /> the version of the tmux executable.
/// <see cref="Server.InspectAsync" /> looks at the live server without running
/// <see cref="ServerConnectionOptions.InitializeAsync" />, answering
/// <c>null</c> when none is listening, and <see cref="Server.DaemonVersion" />
/// carries the daemon version it read.
/// <see cref="Server.IsAliveAsync" /> asks whether a server is answering and
/// never reports a failure, <see cref="Server.ThrowIfDeadAsync" /> throws when
/// none is, and <see cref="Server.StartServerAsync" /> starts the daemon
/// without creating a session. <see cref="Server.Within" /> bounds every
/// command by a timeout, and
/// <see cref="Server.EnterControlModeAsync(string, CancellationToken)" /> opens
/// a control client that reports what tmux does.
/// </para>
/// <para>
/// <b>Finding one thing. </b><see cref="Server.GetSessionAsync" />,
/// <see cref="Server.GetWindowAsync" />, and <see cref="Server.GetPaneAsync" />
/// read one object by its identifier and throw
/// <see cref="TmuxObjectNotFoundException" /> when it is absent;
/// <see cref="Server.FindSessionAsync" />,
/// <see cref="Server.FindWindowAsync" />, and
/// <see cref="Server.FindPaneAsync" /> answer <c>null</c> instead, but only
/// after a read that succeeded. <see cref="Server.HasSessionAsync" /> asks by
/// name whether a session exists, and the static
/// <see cref="Client.GetAsync" /> on <see cref="Client" /> finds a client by
/// name.
/// </para>
/// <para>
/// <b>Listing everything. </b><see cref="Server.GetSessionsAsync" />,
/// <see cref="Server.GetWindowsAsync" />, <see cref="Server.GetPanesAsync" />,
/// and <see cref="Server.GetClientsAsync" /> each read all of their kind on the
/// server and throw when the listing fails, so an outage never reads as an
/// empty server. <see cref="Server.GetAttachedSessionsAsync" /> keeps the
/// sessions that have a client attached.
/// <see cref="Server.CaptureSnapshotAsync(SnapshotDepth, CancellationToken)" />
/// reads one listing per level, down to the depth asked for, and answers a new
/// handle whose <see cref="Server.Sessions" />, <see cref="Server.Windows" />,
/// and <see cref="Server.Panes" /> then answer without another command;
/// <see cref="Server.SnapshotMetadata" /> records how the capture went. Clients
/// are not part of a capture, so <see cref="Server.Clients" /> stays
/// uncaptured.
/// </para>
/// <para>
/// <b>Querying things and getting around. </b>
/// <see cref="Server.SearchSessionsAsync" />,
/// <see cref="Server.SearchWindowsAsync" />,
/// <see cref="Server.SearchPanesAsync" />, and
/// <see cref="Server.SearchClientsAsync" /> hand tmux an
/// <see cref="UnsafeTmuxFilter" />, so rows it rejects never become objects;
/// tmux reads the text itself, and a malformed filter returns no rows rather
/// than an error. To filter what has already been read, give the
/// <c>Matching</c> extension in <see cref="Query.QueryExtensions" /> a C#
/// predicate. The same class turns a predicate into a portable document with
/// <see cref="Query.QueryExtensions.Translate" /> and plans it for tmux to
/// evaluate with <see cref="Query.QueryExtensions.Plan" />. To move between
/// levels, read a session's <see cref="Session.Windows" /> and
/// <see cref="Session.Panes" /> and a window's <see cref="Window.Panes" /> to
/// go down; a window's <see cref="Window.Session" /> and a pane's
/// <see cref="Pane.Window" /> and <see cref="Pane.Session" /> to go up; and a
/// session's <see cref="Session.ActiveWindow" /> and
/// <see cref="Session.ActivePane" /> and a window's
/// <see cref="Window.ActivePane" /> to reach the focused child. Reading any of
/// these never reaches tmux, and one the capture did not read throws
/// <see cref="IncompleteSnapshotException" /> rather than reading as empty.
/// </para>
/// <para>
/// <b>Changing things. </b><see cref="Server.CreateSessionAsync" /> makes a
/// session, <see cref="Server.CreateSessionWithReceiptAsync" /> also hands back
/// the identifiers of its first window and pane, and
/// <see cref="Server.CreateOwnedSessionAsync" /> returns a scope that stops the
/// session when disposed. <see cref="Server.KillSessionAsync" /> stops one
/// session by name, <see cref="Server.KillAsync" /> stops the server and counts
/// one that is already gone as success, and <see cref="Server.LockAsync" />
/// locks every client attached to it. Windows and panes change through
/// <see cref="Session" />, <see cref="Window" />, and <see cref="Pane" />.
/// </para>
/// <para>
/// <b>Options and hooks. </b><see cref="Server.Options" /> holds the server's
/// options, <see cref="Server.Hooks" /> its global hooks, and
/// <see cref="Server.Environment" /> the variables new sessions inherit.
/// </para>
/// <para>
/// <b>Everything else tmux keeps. </b>Paste buffers
/// (<see cref="Server.Buffers" />), key bindings (<see cref="Server.Keys" />),
/// configuration (<see cref="Server.SourceFileAsync" />), shell commands
/// (<see cref="Server.RunShellAsync" />, <see cref="Server.IfShellAsync" />),
/// wait-for channels (<see cref="Server.WaitForAsync" />, and
/// <see cref="Server.OpenWaitChannel" /> when a wait needs a deadline),
/// messages and formats (<see cref="Server.DisplayMessageAsync" />,
/// <see cref="Server.GetMessagesAsync" />), the commands this tmux knows
/// (<see cref="Server.GetCommandsAsync" />), command-prompt history
/// (<see cref="Server.GetPromptHistoryAsync" />,
/// <see cref="Server.ClearPromptHistoryAsync" />), other users' access
/// (<see cref="Server.ConfigureAccessAsync" />), and layout checks
/// (<see cref="Server.ValidateLayoutsAsync" />,
/// <see cref="Server.IsValidLayoutCandidate" />).
/// </para>
/// <para>
/// <b>Things that need a terminal. </b><see cref="Server.ShowMenuAsync" />,
/// <see cref="Server.ShowCommandPromptAsync" />, and
/// <see cref="Server.ConfirmBeforeAsync" /> draw on an attached client, and
/// <see cref="Server.SwitchClientAsync" /> moves the caller's own to another
/// session; tmux refuses all four when no client is attached.
/// <see cref="Server.AttachSessionAsync" /> makes the client instead, so it
/// needs a terminal of its own.
/// <see cref="Server.RefreshClientAsync" />,
/// <see cref="Server.SuspendClientAsync" />,
/// <see cref="Server.LockClientAsync" />,
/// <see cref="Server.DetachClientAsync" />, and
/// <see cref="Server.DetachAllClientsAsync" /> take the name of a client, which
/// <see cref="Server.GetClientsAsync" /> lists.
/// </para>
/// <para>
/// Anything tmux can do that is not here is reachable through
/// <see cref="Server.ExecuteCommandAsync" />, which runs one raw command and
/// hands back its result, or <see cref="Server.Chain" />, which runs several in
/// one tmux invocation. A process the library cannot dispatch itself, such as
/// an interactive attach, takes <see cref="Server.EndpointArguments" /> to
/// select the endpoint and <see cref="Server.BuildGuardedCommandLine" /> to
/// build a command line that runs only while the server generation is still
/// current.
/// </para>
/// </remarks>
public sealed partial class Server
{
}
