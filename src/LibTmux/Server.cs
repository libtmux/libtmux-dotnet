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
/// <b>Connecting.</b> Open a handle without running tmux, then connect it to
/// find the server already listening; connecting never starts one. Take the
/// server this process is already inside, or start one and own it, so that
/// disposing the scope stops it. A connected handle is bound to the server it
/// found, so after tmux restarts its sessions, windows, and panes throw
/// <see cref="StaleServerGenerationException" /> rather than reach the new one.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Open: <see cref="Server.Open" />,
/// <see cref="Server.ConnectionOptions" /></description>
/// </item>
/// <item>
/// <description>Connect:
/// <see cref="Server.ConnectAsync(ServerConnectionOptions, CancellationToken)" />,
/// <see cref="Server.ConnectAsync(CancellationToken)" /></description>
/// </item>
/// <item>
/// <description>Inside tmux:
/// <see cref="Server.FromEnvironment" /></description>
/// </item>
/// <item>
/// <description>Own: <see cref="Server.CreateOwnedAsync" /></description>
/// </item>
/// <item>
/// <description>Timeout: <see cref="Server.Within" /></description>
/// </item>
/// <item>
/// <description>Control mode:
/// <see cref="Server.EnterControlModeAsync(string, CancellationToken)" />
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Checking the server.</b> Ask whether a server is answering, or have the
/// call throw when none is. Inspecting reads the live server without running
/// <see cref="ServerConnectionOptions.InitializeAsync" />, and answers
/// <c>null</c> when none is listening. Starting the daemon returns no handle,
/// because with tmux's exit-empty default a server holding no sessions exits
/// as soon as it starts.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Ask: <see cref="Server.IsAliveAsync" />,
/// <see cref="Server.ThrowIfDeadAsync" />,
/// <see cref="Server.InspectAsync" /></description>
/// </item>
/// <item>
/// <description>Start: <see cref="Server.StartServerAsync" /></description>
/// </item>
/// <item>
/// <description>Which server: <see cref="Server.IsMaterialized" />,
/// <see cref="Server.Generation" /></description>
/// </item>
/// <item>
/// <description>Which tmux: <see cref="Server.Version" />,
/// <see cref="Server.DaemonVersion" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Finding one thing.</b> Read one session, window, or pane by its
/// identifier. The Get methods throw
/// <see cref="TmuxObjectNotFoundException" /> when it is absent; the Find
/// methods answer <c>null</c> instead, but only after a read that succeeded.
/// Ask by name whether a session exists, or find a client by name.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Get: <see cref="Server.GetSessionAsync" />,
/// <see cref="Server.GetWindowAsync" />,
/// <see cref="Server.GetPaneAsync" /></description>
/// </item>
/// <item>
/// <description>Find: <see cref="Server.FindSessionAsync" />,
/// <see cref="Server.FindWindowAsync" />,
/// <see cref="Server.FindPaneAsync" /></description>
/// </item>
/// <item>
/// <description>By name: <see cref="Server.HasSessionAsync" />,
/// <see cref="Client.GetAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Listing everything.</b> Read every session, window, pane, or client on
/// the server. Each throws when the listing fails, so an outage never reads as
/// an empty server. A capture reads one listing per level, down to the depth
/// asked for, and answers a new handle whose relations then answer without
/// another command; clients are not part of a capture.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Read: <see cref="Server.GetSessionsAsync" />,
/// <see cref="Server.GetWindowsAsync" />,
/// <see cref="Server.GetPanesAsync" /></description>
/// </item>
/// <item>
/// <description>Attached: <see cref="Server.GetClientsAsync" />,
/// <see cref="Server.GetAttachedSessionsAsync" /></description>
/// </item>
/// <item>
/// <description>Capture:
/// <see cref="Server.CaptureSnapshotAsync(SnapshotDepth, CancellationToken)" />,
/// <see cref="Server.SnapshotMetadata" /></description>
/// </item>
/// <item>
/// <description>Captured: <see cref="Server.Sessions" />,
/// <see cref="Server.Windows" />, <see cref="Server.Panes" />,
/// <see cref="Server.Clients" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Querying things and getting around.</b> Searching hands tmux an
/// <see cref="UnsafeTmuxFilter" />, so rows it rejects never become objects; a
/// malformed filter returns no rows rather than an error, and searching clients
/// needs tmux 3.4. To filter what has already been read, give the
/// <c>Matching</c> extension in <see cref="Query.QueryExtensions" /> a C#
/// predicate; the same class translates a predicate into a portable document
/// and plans it for tmux to evaluate. Moving between levels of a capture never
/// reaches tmux, and a relation the capture did not read throws
/// <see cref="IncompleteSnapshotException" /> rather than reading as empty.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Search: <see cref="Server.SearchSessionsAsync" />,
/// <see cref="Server.SearchWindowsAsync" />,
/// <see cref="Server.SearchPanesAsync" />,
/// <see cref="Server.SearchClientsAsync" /></description>
/// </item>
/// <item>
/// <description>Translate and plan:
/// <see cref="Query.QueryExtensions.Translate" />,
/// <see cref="Query.QueryExtensions.Plan" /></description>
/// </item>
/// <item>
/// <description>Down: <see cref="Session.Windows" />,
/// <see cref="Session.Panes" />, <see cref="Window.Panes" /></description>
/// </item>
/// <item>
/// <description>Up: <see cref="Window.Session" />, <see cref="Pane.Window" />,
/// <see cref="Pane.Session" /></description>
/// </item>
/// <item>
/// <description>Focused: <see cref="Session.ActiveWindow" />,
/// <see cref="Session.ActivePane" />,
/// <see cref="Window.ActivePane" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Changing things.</b> Create a session, with the identifiers of its first
/// window and pane in the receipt form, or inside a scope that stops it when
/// disposed. Creating a session under a name already taken throws
/// <see cref="TmuxSessionExistsException" />. Stopping the server counts one
/// that is already gone as success; windows and panes change through
/// <see cref="Session" />, <see cref="Window" />, and <see cref="Pane" />.
/// </para>
/// <para>
/// <see cref="Server.CreateSessionAsync" />,
/// <see cref="Server.CreateSessionWithReceiptAsync" />,
/// <see cref="Server.CreateOwnedSessionAsync" />,
/// <see cref="Server.KillSessionAsync" />, <see cref="Server.KillAsync" />,
/// <see cref="Server.LockAsync" />
/// </para>
/// <para>
/// <b>Options and hooks.</b> The server's options, its global hooks, and the
/// environment new sessions inherit are objects of their own:
/// <see cref="TmuxOptions" />, <see cref="TmuxHooks" />, and
/// <see cref="TmuxEnvironment" />. Server options belong to the daemon rather
/// than to anything inside it, so nothing is targeted. tmux has no server hook
/// table, so the global one serves as the server's.
/// </para>
/// <para>
/// <see cref="Server.Options" />, <see cref="Server.Hooks" />,
/// <see cref="Server.Environment" />
/// </para>
/// <para>
/// <b>Everything else tmux keeps.</b> Cancelling a shell command stops the call
/// from waiting, not the command from running. Prefer an open wait channel to a
/// plain wait whenever the wait has a deadline, because cancelling a plain wait
/// leaves its queue entry in tmux before 3.8, and that entry takes the next
/// signal. A layout check at parse time never contacts tmux; the check against
/// a server also catches names that only some tmux versions accept.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Buffers and keys: <see cref="Server.Buffers" />,
/// <see cref="Server.Keys" /></description>
/// </item>
/// <item>
/// <description>Configuration:
/// <see cref="Server.SourceFileAsync" /></description>
/// </item>
/// <item>
/// <description>Shell: <see cref="Server.RunShellAsync" />,
/// <see cref="Server.IfShellAsync" /></description>
/// </item>
/// <item>
/// <description>Channels: <see cref="Server.WaitForAsync" />,
/// <see cref="Server.OpenWaitChannel" /></description>
/// </item>
/// <item>
/// <description>Messages: <see cref="Server.DisplayMessageAsync" />,
/// <see cref="Server.GetMessagesAsync" /></description>
/// </item>
/// <item>
/// <description>Commands and prompts: <see cref="Server.GetCommandsAsync" />,
/// <see cref="Server.GetPromptHistoryAsync" />,
/// <see cref="Server.ClearPromptHistoryAsync" /></description>
/// </item>
/// <item>
/// <description>Access:
/// <see cref="Server.ConfigureAccessAsync" /></description>
/// </item>
/// <item>
/// <description>Layouts: <see cref="Server.ValidateLayoutsAsync" />,
/// <see cref="Server.IsValidLayoutCandidate" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Things that need a terminal.</b> Menus, command prompts, and
/// confirmations draw on an attached client, and switching the caller's own
/// client acts on one; tmux refuses all four when none is attached. Attaching
/// makes the client instead, so it needs a terminal of its own. The other
/// client commands take the name of a client, which the client listing returns.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Draw: <see cref="Server.ShowMenuAsync" />,
/// <see cref="Server.ShowCommandPromptAsync" />,
/// <see cref="Server.ConfirmBeforeAsync" /></description>
/// </item>
/// <item>
/// <description>Switch: <see cref="Server.SwitchClientAsync" /></description>
/// </item>
/// <item>
/// <description>Attach: <see cref="Server.AttachSessionAsync" /></description>
/// </item>
/// <item>
/// <description>Refresh or suspend: <see cref="Server.RefreshClientAsync" />,
/// <see cref="Server.SuspendClientAsync" /></description>
/// </item>
/// <item>
/// <description>Lock or detach: <see cref="Server.LockClientAsync" />,
/// <see cref="Server.DetachClientAsync" />,
/// <see cref="Server.DetachAllClientsAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Raw commands.</b> Anything tmux can do that is not here runs as a raw
/// command, one at a time with its result handed back, or several chained into
/// one tmux invocation. A process the library cannot dispatch itself, such as
/// an interactive attach, takes the endpoint arguments to select the endpoint
/// and a guarded command line that runs only while the server generation is
/// still current.
/// </para>
/// <para>
/// <see cref="Server.ExecuteCommandAsync" />, <see cref="Server.Chain" />,
/// <see cref="Server.EndpointArguments" />,
/// <see cref="Server.BuildGuardedCommandLine" />
/// </para>
/// </remarks>
public sealed partial class Server
{
}
