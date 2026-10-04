namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Starts server reads and queries with the caller's cancellation token.</summary>
[<RequireQualifiedAccess>]
module Server =
    /// <summary>Starts a server on the socket the options name and owns it; disposing the scope stops it.</summary>
    /// <remarks>
    /// The core's <c>Server.CreateOwnedAsync</c>, named so F# need not qualify
    /// the type this module shares a name with. A server already listening on
    /// the default socket is refused rather than owned.
    /// </remarks>
    /// <exception cref="T:System.InvalidOperationException">A server is already listening on the default socket.</exception>
    /// <exception cref="T:LibTmux.TmuxCommandException">tmux failed to say whether a server is listening, such as on a socket it may not open.</exception>
    val createOwned: cancellationToken: CancellationToken -> options: ServerConnectionOptions -> Task<OwnedServerScope>

    /// <summary>Attaches to a server already listening on the socket the options name.</summary>
    /// <remarks>The core's <c>Server.ConnectAsync</c>; it never starts a server.</remarks>
    val connect: cancellationToken: CancellationToken -> options: ServerConnectionOptions -> Task<LibTmux.Server>

    /// <summary>Queries every session.</summary>
    /// <remarks>Child windows and panes require an explicit capture at the corresponding depth.</remarks>
    val sessions: server: LibTmux.Server -> Query<LibTmux.Session>

    /// <summary>Queries window placements across all sessions.</summary>
    /// <remarks>A linked window appears once for each session it is linked into.</remarks>
    val windows: server: LibTmux.Server -> Query<LibTmux.Window>

    /// <summary>Queries every pane.</summary>
    val panes: server: LibTmux.Server -> Query<LibTmux.Pane>

    /// <summary>Queries attached clients.</summary>
    /// <remarks>tmux narrows a filtered client listing only from tmux 3.4; older tmux lists every client.</remarks>
    val clients: server: LibTmux.Server -> Query<LibTmux.Client>

    /// <summary>Returns the server with every command bounded by a timeout, for it and every handle taken from it.</summary>
    /// <remarks>
    /// The handle shares the connection, so nothing starts or is verified again.
    /// A command that outlasts the bound fails as <c>MayHaveRun</c>; a caller's
    /// own cancellation still reads as cancellation.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The timeout does not run forward.</exception>
    val within: timeout: TimeSpan -> server: LibTmux.Server -> LibTmux.Server

    /// <summary>Creates a session as described: its windows, and each window's splits.</summary>
    /// <remarks>
    /// <para>
    /// tmux gives a new session one window, so the first <c>WindowSpec</c> is
    /// that window: its name, command and directory go into the command that
    /// creates the session. tmux sets environment there for the whole session,
    /// so the first window's must be empty; put it in the session's. Each
    /// later spec creates a window of its own. A window's splits are made in
    /// order, each beside the pane before it.
    /// </para>
    /// <para>
    /// Steps run one after another; a failure part way leaves what was already
    /// created, so kill the session by name to clean up.
    /// </para>
    /// </remarks>
    /// <returns>The session, read again after its windows and panes exist.</returns>
    /// <exception cref="T:System.ArgumentException">
    /// The session and its first window name different directories, the
    /// first window sets an environment, or a split's size is out of range.
    /// </exception>
    /// <exception cref="T:LibTmux.TmuxSessionExistsException">The name is already taken.</exception>
    val newSession:
        cancellationToken: CancellationToken -> spec: SessionSpec -> server: LibTmux.Server -> Task<LibTmux.Session>

    /// <summary>Returns a new server handle captured to the requested depth.</summary>
    /// <remarks>Acquisition is not atomic; retained handles do not refresh themselves.</remarks>
    val capture:
        cancellationToken: CancellationToken -> depth: SnapshotDepth -> server: LibTmux.Server -> Task<LibTmux.Server>

    /// <summary>Returns a session or None after a successful lookup establishes absence.</summary>
    /// <remarks>Connection, command and cancellation errors propagate unchanged.</remarks>
    val tryFindSession:
        cancellationToken: CancellationToken -> id: SessionId -> server: LibTmux.Server -> Task<LibTmux.Session option>

    /// <summary>Returns a window or None after a successful lookup establishes absence.</summary>
    /// <remarks>Connection, command and cancellation errors propagate unchanged.</remarks>
    val tryFindWindow:
        cancellationToken: CancellationToken -> id: WindowId -> server: LibTmux.Server -> Task<LibTmux.Window option>

    /// <summary>Returns a pane or None after a successful lookup establishes absence.</summary>
    /// <remarks>Connection, command and cancellation errors propagate unchanged.</remarks>
    val tryFindPane:
        cancellationToken: CancellationToken -> id: PaneId -> server: LibTmux.Server -> Task<LibTmux.Pane option>

    /// <summary>Returns the client with an exact name or None after a successful listing finds no match.</summary>
    /// <remarks>Connection, command and cancellation errors propagate unchanged.</remarks>
    /// <exception cref="T:System.ArgumentException">The client name is null, empty or whitespace.</exception>
    val tryFindClient:
        cancellationToken: CancellationToken -> name: string -> server: LibTmux.Server -> Task<LibTmux.Client option>

/// <summary>Starts queries confined to one session.</summary>
[<RequireQualifiedAccess>]
module Session =
    /// <summary>Queries the window placements in a session.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The session was not read through a server.</exception>
    val windows: session: LibTmux.Session -> Query<LibTmux.Window>

    /// <summary>Queries the panes of every window in a session.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The session was not read through a server.</exception>
    val panes: session: LibTmux.Session -> Query<LibTmux.Pane>

    /// <summary>Reads from tmux the pane the session shows: its current window's active pane.</summary>
    /// <remarks>The core's <c>Session.GetActivePaneAsync</c>; a new session's only pane is this one.</remarks>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux reports no such pane.</exception>
    val activePane: cancellationToken: CancellationToken -> session: LibTmux.Session -> Task<LibTmux.Pane>

    /// <summary>Keeps the control client that waits on the session's panes use attached until the handle is disposed.</summary>
    /// <remarks>
    /// The core's <c>Session.HoldWaitClientAsync</c>. Each wait attaches a client and lets it go when it ends;
    /// holding one across a series of waits saves that attach for each. Use it with <c>use!</c>.
    /// </remarks>
    val holdWaitClient: cancellationToken: CancellationToken -> session: LibTmux.Session -> Task<IAsyncDisposable>

/// <summary>Identifies window placements and starts queries confined to one window.</summary>
[<RequireQualifiedAccess>]
module Window =
    /// <summary>Returns a comparable key including the captured session and window index.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The placement was not captured.</exception>
    val placementKey: window: LibTmux.Window -> WindowPlacementKey

    /// <summary>Queries the panes in a window.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The window was not read through a server.</exception>
    val panes: window: LibTmux.Window -> Query<LibTmux.Pane>

    /// <summary>Reads from tmux the window's active pane.</summary>
    /// <remarks>The core's <c>Window.GetActivePaneAsync</c>.</remarks>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux reports no such pane.</exception>
    val activePane: cancellationToken: CancellationToken -> window: LibTmux.Window -> Task<LibTmux.Pane>

/// <summary>Reads captured pane fields and starts explicit pane operations.</summary>
[<RequireQualifiedAccess>]
module Pane =
    /// <summary>Reads the captured working directory, preserving an empty string.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The path field was not captured.</exception>
    val currentPath: pane: LibTmux.Pane -> string option

    /// <summary>Reads the captured command name, preserving an empty string.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The command field was not captured.</exception>
    val currentCommand: pane: LibTmux.Pane -> string option

    /// <summary>Captures pane contents using the supplied core request.</summary>
    val capture:
        cancellationToken: CancellationToken ->
        request: CapturePaneRequest ->
        pane: LibTmux.Pane ->
            Task<IReadOnlyList<string>>

    /// <summary>Reads what the pane printed since a position, and where this read finished.</summary>
    /// <remarks>
    /// The core's <c>Pane.ReadOutputSinceAsync</c>. Start with <c>None</c>, which returns no lines and a
    /// position; pass each result's <c>Position</c> to the next read. <c>LinesMissed</c> says scrollback
    /// dropped output first. This is the MCP server's <c>capture_since</c>.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The position came from another pane.</exception>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane runs a different program than when the position was taken, or a read without a position found its program exited.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val readSince:
        cancellationToken: CancellationToken ->
        position: PaneOutputPosition option ->
        pane: LibTmux.Pane ->
            Task<PaneOutputSince>

    /// <summary>Returns the first visible row showing the text, counted from 1, or None.</summary>
    /// <remarks>tmux searches only the rows on screen. Capture the history and filter its lines to search further back.</remarks>
    /// <exception cref="T:System.ArgumentException">The text cannot be written as a tmux format.</exception>
    val findOnScreen:
        cancellationToken: CancellationToken -> search: ScreenSearch -> pane: LibTmux.Pane -> Task<int option>

    /// <summary>Waits for a line the pane prints to contain the text.</summary>
    /// <remarks>
    /// Text already on screen ends the wait at once as <c>PresentAtEntry</c>.
    /// The wait sleeps on the pane's own output rather than polling, and ends
    /// early when the pane's program exits or a full-screen program starts.
    /// Running out of time returns the outcome <c>TimedOut</c>; only <c>Mirror.waitUntil</c> raises instead.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The text is empty or spans lines.</exception>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val waitForText:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        text: string ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Waits as the request describes: patterns, stop patterns, or any output.</summary>
    /// <remarks>Running out of time returns the outcome <c>TimedOut</c>; only <c>Mirror.waitUntil</c> raises instead.</remarks>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val waitFor:
        cancellationToken: CancellationToken -> request: PaneWaitRequest -> pane: LibTmux.Pane -> Task<PaneWaitResult>

    /// <summary>Types a line, presses Enter, and waits for a later line to contain the text.</summary>
    /// <remarks>
    /// The screen before the line is typed never ends the wait, and the
    /// shell's echo of the line is discounted: waiting for <c>done</c> after
    /// typing <c>echo done</c> waits for the command's output. Prefer this to
    /// <c>sendKeys</c> followed by <c>waitForText</c>, which can match the
    /// typed line itself.
    /// Running out of time returns the outcome <c>TimedOut</c>; only <c>Mirror.waitUntil</c> raises instead.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The text is empty or spans lines.</exception>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited, or the pane changed during every read until the timeout, so nothing was sent.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val sendAndWait:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        line: string ->
        text: string ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Sends keys as the request describes, then waits as the wait request describes.</summary>
    /// <remarks>
    /// As <c>sendAndWait</c>: only output after the keys counts, and literal
    /// text is discounted from it. Key names are not.
    /// Running out of time returns the outcome <c>TimedOut</c>; only <c>Mirror.waitUntil</c> raises instead.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The wait names no pattern.</exception>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited, or the pane changed during every read until the timeout, so nothing was sent.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val sendAndWaitFor:
        cancellationToken: CancellationToken ->
        keys: SendKeysRequest ->
        request: PaneWaitRequest ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Waits until a condition holds over the rows the pane shows, top to bottom.</summary>
    /// <remarks>
    /// The condition sees the whole screen each time the pane prints or changes state.
    /// Running out of time returns the outcome <c>TimedOut</c>; only <c>Mirror.waitUntil</c> raises instead.
    /// </remarks>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    val waitUntil:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        condition: (IReadOnlyList<string> -> bool) ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Runs a shell command in the pane and waits for its exit status and output.</summary>
    /// <remarks>
    /// The pane must sit at a POSIX shell prompt. A command still running at
    /// the timeout keeps running; the result reports <c>TimedOut</c>. A command
    /// that prints more than scrollback holds reports <c>LinesMissed</c>, and its
    /// <c>Output</c> is then what the pane still showed.
    /// </remarks>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane is in a mode, not running a POSIX shell, or its program has exited; or it changed during every read before the command was sent.</exception>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="T:LibTmux.LibTmuxException">The command was sent and the run was cancelled or could not be observed; <c>TmuxFailure.MayHaveRun</c> matches it, and the pane needs inspecting before a retry.</exception>
    val run:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        command: string ->
        pane: LibTmux.Pane ->
            Task<PaneRunResult>

    /// <summary>Types a line into the pane as literal text, then presses Enter.</summary>
    /// <exception cref="T:System.ArgumentException">The line contains NUL.</exception>
    /// <exception cref="T:LibTmux.LibTmuxException">The text was sent but Enter failed; whether tmux pressed it is unknown, so do not send the line again.</exception>
    val sendLine: cancellationToken: CancellationToken -> line: string -> pane: LibTmux.Pane -> Task

    /// <summary>Types text into the pane literally, without pressing Enter.</summary>
    /// <exception cref="T:System.ArgumentException">The text contains NUL.</exception>
    val sendText: cancellationToken: CancellationToken -> text: string -> pane: LibTmux.Pane -> Task

    /// <summary>Presses one key by its tmux name, such as <c>Enter</c>, <c>C-c</c> or <c>Up</c>.</summary>
    /// <remarks>tmux types a name it does not know as text. Cancellation can occur after dispatch; it does not undo the key.</remarks>
    /// <exception cref="T:System.ArgumentException">The key is empty or white space.</exception>
    val pressKey: cancellationToken: CancellationToken -> key: string -> pane: LibTmux.Pane -> Task

    /// <summary>Sends text or key names according to the request's literal and Enter settings.</summary>
    /// <remarks>Cancellation can occur after dispatch; it does not undo sent keys.</remarks>
    val sendKeys: cancellationToken: CancellationToken -> request: SendKeysRequest -> pane: LibTmux.Pane -> Task

    /// <summary>Splits the pane and returns the new pane handle.</summary>
    /// <remarks>
    /// It takes the core request, which carries every split-window option;
    /// <c>SplitSpec</c> describes only the splits a <c>Server.newSession</c>
    /// spec builds. Cancellation can leave the split applied; do not retry
    /// automatically.
    /// </remarks>
    val split:
        cancellationToken: CancellationToken -> request: SplitPaneRequest -> pane: LibTmux.Pane -> Task<LibTmux.Pane>

/// <summary>Reads and writes options through keys that know their value's type.</summary>
/// <remarks>
/// Pass the options of the scope the option belongs to, such as
/// <c>session.Options</c> for <c>TmuxOptionKey.HistoryLimit</c>. Declare other keys
/// with <c>TmuxOptionKey.Text</c>, <c>Number</c> or <c>Flag</c>.
/// </remarks>
[<RequireQualifiedAccess>]
module Options =
    /// <summary>Reads the value an option has in a scope, set there or inherited, as its key's type.</summary>
    /// <exception cref="T:LibTmux.TmuxOptionException">tmux rejected the name, reported no value, or reported one the key cannot read.</exception>
    val get: cancellationToken: CancellationToken -> key: TmuxOptionKey<'T> -> options: TmuxOptions -> Task<'T>

    /// <summary>Sets an option in a scope from a value of its key's type.</summary>
    /// <exception cref="T:LibTmux.TmuxOptionException">tmux rejected the name or the value.</exception>
    val set: cancellationToken: CancellationToken -> key: TmuxOptionKey<'T> -> value: 'T -> options: TmuxOptions -> Task

/// <summary>Builds commands tmux runs together, each acting on what the one before made.</summary>
/// <remarks>
/// <para>
/// tmux moves its current target as a chain runs: <c>newWindow</c> makes the
/// new window current, a following split splits its pane, and a following
/// <c>sendLine</c> types into the pane that split made. No step after the
/// first names a target, so a chain needs no round trip to learn the id of
/// what it just created.
/// </para>
/// <para>
/// A chain is a core <c>TmuxChain</c>: each step returns a new one, building
/// reads nothing, and <c>run</c> sends every command in one tmux invocation.
/// <c>add</c> appends any command, such as a typed request's <c>ToCommand</c>.
/// </para>
/// </remarks>
[<RequireQualifiedAccess>]
module Chain =
    /// <summary>Starts an empty chain against a server.</summary>
    /// <exception cref="T:System.InvalidOperationException">The server handle has no connection.</exception>
    val start: server: LibTmux.Server -> TmuxChain

    /// <summary>Adds a window to a session and makes it the one following steps act on.</summary>
    /// <remarks>It fails rather than reach another session when tmux restarted after the session was read.</remarks>
    val newWindow: session: LibTmux.Session -> name: string -> chain: TmuxChain -> TmuxChain

    /// <summary>Splits the current pane into a left and a right one; the right becomes current.</summary>
    val splitLeftRight: chain: TmuxChain -> TmuxChain

    /// <summary>Splits the current pane into a top and a bottom one; the bottom becomes current.</summary>
    val splitTopBottom: chain: TmuxChain -> TmuxChain

    /// <summary>Types a line into the current pane and presses Enter.</summary>
    val sendLine: line: string -> chain: TmuxChain -> TmuxChain

    /// <summary>Arranges the current window with a tmux layout; the chain checks the name before tmux sees it.</summary>
    val arrange: layout: string -> chain: TmuxChain -> TmuxChain

    /// <summary>Appends any command, such as a typed request's <c>ToCommand</c>.</summary>
    val add: command: TmuxCommand -> chain: TmuxChain -> TmuxChain

    /// <summary>Runs every command in one tmux invocation and returns tmux's combined answer.</summary>
    /// <remarks>Cancellation after dispatch does not undo commands tmux already ran.</remarks>
    val run: cancellationToken: CancellationToken -> chain: TmuxChain -> Task<TmuxCommandResult>
