namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Starts server reads and queries with the caller's cancellation token.</summary>
[<RequireQualifiedAccess>]
module Server =
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

/// <summary>Identifies window placements and starts queries confined to one window.</summary>
[<RequireQualifiedAccess>]
module Window =
    /// <summary>Returns a comparable key including the captured session and window index.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The placement was not captured.</exception>
    val placementKey: window: LibTmux.Window -> WindowPlacementKey

    /// <summary>Queries the panes in a window.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The window was not read through a server.</exception>
    val panes: window: LibTmux.Window -> Query<LibTmux.Pane>

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
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The text is empty or spans lines.</exception>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    val waitForText:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        text: string ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Waits as the request describes: patterns, stop patterns, or any output.</summary>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    val waitFor:
        cancellationToken: CancellationToken -> request: PaneWaitRequest -> pane: LibTmux.Pane -> Task<PaneWaitResult>

    /// <summary>Waits until a condition holds over the rows the pane shows, top to bottom.</summary>
    /// <remarks>The condition sees the whole screen each time the pane prints or changes state.</remarks>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane's program had already exited.</exception>
    val waitUntil:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        condition: (IReadOnlyList<string> -> bool) ->
        pane: LibTmux.Pane ->
            Task<PaneWaitResult>

    /// <summary>Runs a shell command in the pane and waits for its exit status and output.</summary>
    /// <remarks>
    /// The pane must sit at a POSIX shell prompt. A command still running at
    /// the timeout keeps running; the result reports <c>TimedOut</c>.
    /// </remarks>
    /// <exception cref="T:LibTmux.TmuxPaneException">The pane is in a mode or not running a POSIX shell.</exception>
    val run:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        command: string ->
        pane: LibTmux.Pane ->
            Task<PaneRunResult>

    /// <summary>Sends text or key names according to the request's literal and Enter settings.</summary>
    /// <remarks>Cancellation can occur after dispatch; it does not undo sent keys.</remarks>
    val sendKeys: cancellationToken: CancellationToken -> request: SendKeysRequest -> pane: LibTmux.Pane -> Task

    /// <summary>Splits the pane and returns the new pane handle.</summary>
    /// <remarks>Cancellation can leave the split applied; do not retry automatically.</remarks>
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
