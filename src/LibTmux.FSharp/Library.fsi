namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Describes a selection that does not contain exactly one match.</summary>
type CardinalityError =
    /// <summary>No element matched.</summary>
    | NoMatches
    /// <summary>At least two elements matched.</summary>
    | MultipleMatches

/// <summary>Distinguishes captured state from a relation the snapshot did not read.</summary>
type CaptureState<'T> =
    /// <summary>Contains the captured value, including an observed empty collection.</summary>
    | Captured of value: 'T
    /// <summary>Names the unread relation and the depth the snapshot reached.</summary>
    | Uncaptured of relation: string * depth: SnapshotDepth

/// <summary>Identifies one indexed placement of a window within a server generation.</summary>
[<StructuralEquality; StructuralComparison>]
type WindowPlacementKey =
    private
        {
            ServerProcessId: int
            ServerStartTime: int64
            SessionId: int
            WindowId: int
            WindowIndex: int
        }

/// <summary>Reads captured values without contacting tmux.</summary>
[<RequireQualifiedAccess>]
module Snapshot =
    /// <summary>Distinguishes captured children from an unread relation.</summary>
    val relation: relation: CapturedRelation<'T> -> CaptureState<IReadOnlyList<'T>>

    /// <summary>Distinguishes a captured child from an unread value.</summary>
    val value: value: CapturedValue<'T> -> CaptureState<'T> when 'T: not struct

/// <summary>Selects values from ordinary F# sequences.</summary>
[<RequireQualifiedAccess>]
module Selection =
    /// <summary>Returns the sole match, examining at most two elements.</summary>
    /// <remarks>Disposes the enumerator on success, multiple matches or failure.</remarks>
    val exactlyOne: source: seq<'T> -> Result<'T, CardinalityError>

/// <summary>Recognises tmux failures by whether running the operation again could repeat what it did.</summary>
/// <remarks>
/// Every <c>LibTmuxException</c> says whether its command reached tmux, so match
/// on that rather than on the exception type. <c>NotSent</c> is the only failure
/// after which running the same command again is always safe. It says nothing
/// about commands sent before it: an operation that ran one command and then
/// failed to send another has already acted. <c>Async.AwaitTask</c> and
/// <c>Task.Wait</c> hand a failure over inside an <c>AggregateException</c>;
/// one holding a single failure is matched as that failure.
/// </remarks>
[<RequireQualifiedAccess>]
module TmuxFailure =
    /// <summary>Matches a failure whose command never reached tmux; running it again repeats nothing.</summary>
    val (|NotSent|_|): error: exn -> LibTmuxException option

    /// <summary>Matches a failure after tmux ran the command: tmux reported an error, or its answer could not be used.</summary>
    /// <remarks>Running the command again repeats whatever it did; a read can simply be read again.</remarks>
    val (|Ran|_|): error: exn -> LibTmuxException option

    /// <summary>Matches a failure, or a cancellation, after which tmux may already have acted.</summary>
    val (|MayHaveRun|_|): error: exn -> exn option

/// <summary>Recognises how a command run with <c>Pane.run</c> ended.</summary>
[<RequireQualifiedAccess>]
module PaneRun =
    /// <summary>Tells how a run ended, one case per kind of ending, so a match that leaves one out draws a warning.</summary>
    /// <remarks>
    /// <c>Exited</c>: the command exited, carried as its exit status.
    /// <c>Ended</c>: the pane's program exited before the command reported its status, and the run ended then.
    /// <c>NotStarted</c>: the pane's shell never ran it, such as when the pane was not at a prompt.
    /// <c>TimedOut</c>: the time allowed ran out first; the command may still be running.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The result has none of these endings.</exception>
    val (|Exited|Ended|NotStarted|TimedOut|): result: PaneRunResult -> Choice<int, unit, unit, unit>

/// <summary>Recognises how a wait on a pane's output ended.</summary>
[<RequireQualifiedAccess>]
module PaneWait =
    /// <summary>Tells how a wait ended, one case per kind of ending, so a match that leaves one out draws a warning.</summary>
    /// <remarks>
    /// <c>Found</c>: the text or a pattern appeared, before or during the wait.
    /// <c>Printed</c>: a wait with no pattern saw the pane print something.
    /// <c>Stopped</c>: a stop pattern matched, carried as its text.
    /// <c>TimedOut</c>: the time allowed ran out.
    /// <c>Ended</c>: the pane's program exited, or a full-screen program took over.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The outcome is not one this facade knows.</exception>
    val (|Found|Printed|Stopped|TimedOut|Ended|): result: PaneWaitResult -> Choice<unit, unit, string, unit, unit>

/// <summary>Recognises what a pane watch yields.</summary>
[<RequireQualifiedAccess>]
module PaneWatch =
    /// <summary>Tells what <c>Control.watchPane</c> or <c>Control.watchPanes</c> yielded, one case per kind of event, so a match that leaves one out draws a warning.</summary>
    /// <remarks>
    /// <c>Output</c>: text a watched pane printed, carried as its event.
    /// <c>Paused</c>: tmux stopped sending that pane's output, so what it prints until <c>Continued</c> never arrives; capture the pane to read its screen.
    /// <c>Continued</c>: tmux resumed sending that pane's output.
    /// <c>Dropped</c>: a full buffer discarded events, carried as the loss report.
    /// <c>Gone</c>: a watched pane is gone; the watch ends once every pane is.
    /// <c>Exited</c>: the control client ended, with tmux's reason when it gave one; the watch ends.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The event is not one a pane watch yields.</exception>
    val (|Output|Paused|Continued|Dropped|Gone|Exited|):
        event: TmuxEvent -> Choice<TmuxOutputEvent, PaneId, PaneId, TmuxEventsDroppedEvent, PaneId, string option>

/// <summary>Awaits tasks in an <c>async</c> workflow without losing whether tmux may have acted.</summary>
/// <remarks>
/// <c>Async.AwaitTask</c> turns a <c>TmuxOperationCanceledException</c> into a bare
/// <c>TaskCanceledException</c>, losing <c>CommandMayHaveExecuted</c>, and wraps a failure in an
/// <c>AggregateException</c>. These raise a tmux client cancelled after it may have acted as itself,
/// so <c>TmuxFailure.MayHaveRun</c> matches it in <c>try ... with</c>; any other cancellation cancels
/// the workflow, and a failure is raised as the task raised it.
/// </remarks>
[<RequireQualifiedAccess>]
module TmuxAsync =
    /// <summary>Awaits a task that returns a value.</summary>
    val awaitTask: task: Task<'T> -> Async<'T>

    /// <summary>Awaits a task that returns nothing.</summary>
    val awaitUnitTask: task: Task -> Async<unit>

/// <summary>Runs an operation again only when tmux never saw it.</summary>
[<RequireQualifiedAccess>]
module Retry =
    /// <summary>Runs an operation, and again up to <c>retries</c> times while nothing it sent reached tmux.</summary>
    /// <remarks>
    /// An attempt is repeated only when it fails with <c>NotSent</c> and no
    /// command it sent before that failure reached tmux, counting every command
    /// the operation awaits, including through nested retries. An operation of
    /// several steps whose first step ran is therefore not repeated because a
    /// later step was refused before dispatch. Any other failure, and
    /// cancellation, propagates at once. A read that is safe to repeat whatever
    /// happened belongs in the caller's own retry policy.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The retry count is negative.</exception>
    val ifNotSent:
        cancellationToken: CancellationToken -> retries: int -> operation: (CancellationToken -> Task<'T>) -> Task<'T>

    /// <summary>Runs an operation, and after each delay in turn runs it again while nothing it sent reached tmux.</summary>
    /// <remarks>
    /// Retries as <c>ifNotSent</c> does, once per delay, waiting that long
    /// first, so a server still starting has time to answer:
    /// <c>Retry.ifNotSentAfter ct [ TimeSpan.FromMilliseconds 100.; TimeSpan.FromMilliseconds 400. ] operation</c>.
    /// Cancellation during a delay propagates.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">A delay is negative.</exception>
    val ifNotSentAfter:
        cancellationToken: CancellationToken ->
        delays: TimeSpan list ->
        operation: (CancellationToken -> Task<'T>) ->
            Task<'T>

module internal Placement =
    val key: window: LibTmux.Window -> WindowPlacementKey
