namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Represents a decision to continue or stop an event fold.</summary>
[<RequireQualifiedAccess>]
type StreamStep<'State> =
    /// <summary>Retains state and reads the next event.</summary>
    | Continue of state: 'State
    /// <summary>Retains state and stops before reading another event.</summary>
    | Stop of state: 'State

/// <summary>Opens control clients and reads their event streams.</summary>
/// <remarks>
/// Streams are cold: nothing is read until a consumer enumerates one, and the
/// consumer supplies the cancellation token. Any <c>IAsyncEnumerable</c>
/// library composes them, including FSharp.Control.TaskSeq.
/// </remarks>
[<RequireQualifiedAccess>]
module Control =
    /// <summary>Opens a control client attached to the most recently used session.</summary>
    /// <remarks>
    /// The caller owns and asynchronously disposes the returned client. On a
    /// server with several sessions, <c>enterSession</c> attaches to a chosen one.
    /// </remarks>
    val enter: cancellationToken: CancellationToken -> server: LibTmux.Server -> Task<IControlModeSession>

    /// <summary>Opens a control client attached to a session.</summary>
    /// <remarks>The caller owns and asynchronously disposes the returned client.</remarks>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The session was not read through a server.</exception>
    val enterSession: cancellationToken: CancellationToken -> session: LibTmux.Session -> Task<IControlModeSession>

    /// <summary>Runs work with an owned control client and disposes it after the returned task completes.</summary>
    /// <remarks>The work function receives the client and must forward its own cancellation token.</remarks>
    val useSession: work: (IControlModeSession -> Task<'State>) -> session: IControlModeSession -> Task<'State>

    /// <summary>Opens a control client, runs work, and disposes the client after the returned task completes.</summary>
    /// <remarks>
    /// The cancellation token starts the client; the work function forwards its
    /// own token. Like <c>enter</c>, the client attaches to the most recently
    /// used session; for a chosen one, pass <c>enterSession</c>'s client to
    /// <c>useSession</c>.
    /// </remarks>
    val withSession:
        cancellationToken: CancellationToken ->
        work: (IControlModeSession -> Task<'State>) ->
        server: LibTmux.Server ->
            Task<'State>

    /// <summary>Streams every event a control client reports.</summary>
    /// <remarks>A client has one event stream; reading it while another reader is reading raises <c>InvalidOperationException</c>.</remarks>
    val events: session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>

    /// <summary>Streams one pane's output from a borrowed control client.</summary>
    /// <remarks>
    /// <para>
    /// The stream ends with <c>TmuxPaneGoneEvent</c> once the pane is confirmed
    /// gone, or with <c>TmuxExitEvent</c> when the client ends.
    /// <c>TmuxPanePausedEvent</c> and <c>TmuxPaneContinuedEvent</c> bracket output
    /// a slow reader missed. It reads the client's single event stream, so other
    /// events are consumed and dropped; follow several panes through one client
    /// with <c>watchPanes</c>.
    /// </para>
    /// <para>
    /// tmux discards output it has not yet sent once a pane's program exits,
    /// so the last lines of a program that exits at once may never arrive.
    /// Read final output with <c>Pane.run</c>, or capture a pane kept with
    /// <c>remain-on-exit</c>.
    /// </para>
    /// </remarks>
    val watchPane: pane: LibTmux.Pane -> session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>

    /// <summary>Streams several panes' output from one borrowed control client.</summary>
    /// <remarks>
    /// Each output event names its pane. Each pane confirmed gone is reported by
    /// a <c>TmuxPaneGoneEvent</c> after the output buffered before it went,
    /// unless the client ends first, and the stream ends once every pane is gone,
    /// or with <c>TmuxExitEvent</c> when the client ends. Events after that stay
    /// unread for the client's next reader.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The list is empty.</exception>
    val watchPanes: panes: LibTmux.Pane list -> session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>

    /// <summary>Awaits one handler at a time for each item until the stream ends.</summary>
    /// <remarks>The helper disposes its enumerator but leaves the control client open.</remarks>
    val iter:
        cancellationToken: CancellationToken -> handler: ('T -> Task) -> source: IAsyncEnumerable<'T> -> Task<unit>

    /// <summary>Folds items until the stream ends or the folder returns Stop.</summary>
    /// <remarks>The helper disposes its enumerator but leaves the control client open.</remarks>
    val foldWhile:
        cancellationToken: CancellationToken ->
        folder: ('State -> 'T -> Task<StreamStep<'State>>) ->
        initial: 'State ->
        source: IAsyncEnumerable<'T> ->
            Task<'State>

    /// <summary>Returns the cleanup failure attached to the exception a helper rethrew.</summary>
    /// <remarks>
    /// When work and cleanup both fail, the helpers rethrow the work's exception
    /// unchanged and attach the cleanup's; this reads it back. The work's
    /// exception keeps its type so that a handler written for it, such as
    /// <c>:? TmuxPaneException</c> or a <c>when</c> filter, still matches;
    /// an <c>AggregateException</c> of both would slip past those handlers.
    /// </remarks>
    val cleanupFailure: error: exn -> exn option

/// <summary>Follows a server's sessions, windows, panes and clients as tmux announces changes.</summary>
/// <remarks>
/// Each announcement starts a fresh capture; a capture that finds nothing
/// different publishes nothing. tmux does not announce a pane's running command
/// or working directory, nor layout changes in other sessions; use
/// <c>startRefreshing</c> to see those within an interval.
/// </remarks>
[<RequireQualifiedAccess>]
module Mirror =
    /// <summary>Mirrors the server an anchor session belongs to, capturing on each announcement.</summary>
    /// <remarks>The caller owns and asynchronously disposes the returned mirror.</remarks>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The session was not read through a server.</exception>
    val start: cancellationToken: CancellationToken -> anchor: LibTmux.Session -> Task<ServerMirror>

    /// <summary>Mirrors a server, also capturing whenever it has been quiet for an interval.</summary>
    /// <remarks>The caller owns and asynchronously disposes the returned mirror.</remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The interval is negative.</exception>
    val startRefreshing:
        cancellationToken: CancellationToken -> every: TimeSpan -> anchor: LibTmux.Session -> Task<ServerMirror>

    /// <summary>Returns the latest published view.</summary>
    val current: mirror: ServerMirror -> ServerMirrorView

    /// <summary>Streams the current view and each newer one, skipping views published while the reader was busy.</summary>
    /// <remarks>The stream is cold, ends when the mirror ends, and raises the failure that ended it.</remarks>
    /// <exception cref="T:LibTmux.TmuxObjectNotFoundException">The anchor session has gone, so the mirror could not attach again.</exception>
    val views: mirror: ServerMirror -> IAsyncEnumerable<ServerMirrorView>

    /// <summary>Waits until a view satisfies a condition, testing the current view first.</summary>
    /// <remarks>
    /// A view is published only when something besides activity times, cursor
    /// positions and history sizes changes, so a condition on those alone can
    /// wait for an unrelated change. Wait on output with the pane waits.
    /// </remarks>
    /// <exception cref="T:LibTmux.TmuxWaitTimeoutException">No view satisfied the condition in time.</exception>
    /// <exception cref="T:System.InvalidOperationException">The mirror ended first.</exception>
    val waitUntil:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        condition: (ServerMirrorView -> bool) ->
        mirror: ServerMirror ->
            Task<ServerMirrorView>

    /// <summary>Waits until a view satisfies a condition, or returns None when none did in time.</summary>
    /// <remarks>As <c>waitUntil</c>, for a caller to whom running out of time is an ordinary outcome.</remarks>
    /// <exception cref="T:System.InvalidOperationException">The mirror ended first.</exception>
    val tryWaitUntil:
        cancellationToken: CancellationToken ->
        timeout: TimeSpan ->
        condition: (ServerMirrorView -> bool) ->
        mirror: ServerMirror ->
            Task<ServerMirrorView option>
