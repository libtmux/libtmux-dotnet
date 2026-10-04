namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

type CardinalityError =
    | NoMatches
    | MultipleMatches

type CaptureState<'T> =
    | Captured of value: 'T
    | Uncaptured of relation: string * depth: SnapshotDepth

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

[<RequireQualifiedAccess>]
module Snapshot =
    let relation (relation: CapturedRelation<'T>) =
        if relation.IsCaptured then
            Captured(relation :> IReadOnlyList<'T>)
        else
            Uncaptured(relation.Relation, relation.CapturedDepth)

    let value (value: CapturedValue<'T>) =
        if value.IsCaptured then
            Captured value.Value
        else
            Uncaptured(value.Relation, value.CapturedDepth)

[<RequireQualifiedAccess>]
module Selection =
    let exactlyOne (source: seq<'T>) =
        use iterator = source.GetEnumerator()

        if not (iterator.MoveNext()) then
            Error NoMatches
        else
            let first = iterator.Current

            if iterator.MoveNext() then
                Error MultipleMatches
            else
                Ok first

[<RequireQualifiedAccess>]
module TmuxFailure =
    // Async.AwaitTask and Task.Wait hand over a failed task's exception
    // inside an AggregateException; one holding a single failure is that failure.
    let rec private unwrap (error: exn) =
        match error with
        | :? AggregateException as wrapped when wrapped.InnerExceptions.Count = 1 -> unwrap wrapped.InnerExceptions[0]
        | _ -> error

    let (|NotSent|_|) (error: exn) =
        match unwrap error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.NotDispatched -> Some failure
        | _ -> None

    let (|Ran|_|) (error: exn) =
        match unwrap error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.Dispatched -> Some failure
        | _ -> None

    let (|MayHaveRun|_|) (error: exn) =
        match unwrap error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.Unknown -> Some(failure :> exn)
        | :? TmuxOperationCanceledException as canceled when canceled.CommandMayHaveExecuted -> Some(canceled :> exn)
        | _ -> None

[<RequireQualifiedAccess>]
module PaneRun =
    let (|Exited|Ended|NotStarted|TimedOut|) (result: PaneRunResult) =
        match Option.ofNullable result.ExitStatus with
        | Some status -> Exited status
        | None when result.PaneExited -> Ended
        | None when not result.Started -> NotStarted
        | None when result.TimedOut -> TimedOut
        | None ->
            raise (
                ArgumentOutOfRangeException(
                    nameof result,
                    box result,
                    "The run has no exit status and did not time out; this facade knows no such ending."
                )
            )

[<RequireQualifiedAccess>]
module PaneWait =
    let (|Found|Printed|Stopped|TimedOut|Ended|) (result: PaneWaitResult) =
        match result.Outcome with
        | PaneWaitOutcome.Matched
        | PaneWaitOutcome.PresentAtEntry -> Found
        | PaneWaitOutcome.AnyOutput -> Printed
        | PaneWaitOutcome.Stopped -> Stopped(Option.ofObj result.Pattern |> Option.defaultValue "")
        | PaneWaitOutcome.TimedOut -> TimedOut
        | PaneWaitOutcome.PaneExited
        | PaneWaitOutcome.AlternateScreen -> Ended
        | outcome ->
            raise (
                ArgumentOutOfRangeException(nameof result, outcome, "The wait outcome is not one this facade knows.")
            )

[<RequireQualifiedAccess>]
module PaneWatch =
    let (|Output|Paused|Continued|Dropped|Gone|Exited|) (event: TmuxEvent) =
        match event with
        | :? TmuxOutputEvent as output -> Output output
        | :? TmuxPanePausedEvent as paused -> Paused paused.PaneId
        | :? TmuxPaneContinuedEvent as continued -> Continued continued.PaneId
        | :? TmuxEventsDroppedEvent as dropped -> Dropped dropped
        | :? TmuxPaneGoneEvent as gone -> Gone gone.PaneId
        | :? TmuxExitEvent as exit -> Exited(Option.ofObj exit.Reason)
        | other ->
            raise (ArgumentOutOfRangeException(nameof event, box other, "The event is not one a pane watch yields."))

[<RequireQualifiedAccess>]
module TmuxAsync =
    // The outcome is taken before a continuation runs, so an exception the
    // continuation raises is not mistaken for the task's.
    let private settle (result: unit -> 'T) ok (error: exn -> unit) (cancel: OperationCanceledException -> unit) =
        let outcome =
            try
                Ok(result ())
            with
            | :? TmuxOperationCanceledException as kept when kept.CommandMayHaveExecuted ->
                Error(Choice1Of2(kept :> exn))
            | :? OperationCanceledException as canceled -> Error(Choice2Of2 canceled)
            | failure -> Error(Choice1Of2 failure)

        match outcome with
        | Ok value -> ok value
        | Error(Choice1Of2 failure) -> error failure
        | Error(Choice2Of2 canceled) -> cancel canceled

    let awaitTask (task: Task<'T>) : Async<'T> =
        ArgumentNullException.ThrowIfNull task

        Async.FromContinuations(fun (ok, error, cancel) ->
            task.ContinueWith(
                Action<Task<'T>>(fun completed ->
                    settle (fun () -> completed.GetAwaiter().GetResult()) ok error cancel),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore)

    let awaitUnitTask (task: Task) : Async<unit> =
        ArgumentNullException.ThrowIfNull task

        Async.FromContinuations(fun (ok, error, cancel) ->
            task.ContinueWith(
                Action<Task>(fun completed -> settle (fun () -> completed.GetAwaiter().GetResult()) ok error cancel),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore)

[<RequireQualifiedAccess>]
module Retry =
    let private retrying
        (cancellationToken: CancellationToken)
        (delays: TimeSpan list)
        (operation: CancellationToken -> Task<'T>)
        =
        backgroundTask {
            let mutable remaining = delays
            let mutable result = None

            while result.IsNone do
                // A NotSent failure clears only its own command; anything the
                // attempt sent before it may have run.
                let attempt = LibTmux.Internal.TmuxDispatchLedger.Create()
                let mutable retry = false

                try
                    let! value =
                        LibTmux.Internal.TmuxDispatchLedger.CountAsync(attempt, (fun () -> operation cancellationToken))

                    result <- Some value
                with TmuxFailure.NotSent _ when
                    not remaining.IsEmpty
                    && not attempt.AnyReached
                    && not cancellationToken.IsCancellationRequested ->
                    retry <- true

                if retry then
                    let delay = remaining.Head
                    remaining <- remaining.Tail

                    if delay > TimeSpan.Zero then
                        do! Task.Delay(delay, cancellationToken)

            return result.Value
        }

    let ifNotSent (cancellationToken: CancellationToken) (retries: int) (operation: CancellationToken -> Task<'T>) =
        if retries < 0 then
            raise (ArgumentOutOfRangeException(nameof retries, retries, "The retry count is negative."))

        retrying cancellationToken (List.replicate retries TimeSpan.Zero) operation

    let ifNotSentAfter
        (cancellationToken: CancellationToken)
        (delays: TimeSpan list)
        (operation: CancellationToken -> Task<'T>)
        =
        match delays |> List.tryFind (fun delay -> delay < TimeSpan.Zero) with
        | Some negative -> raise (ArgumentOutOfRangeException(nameof delays, negative, "A delay is negative."))
        | None -> retrying cancellationToken delays operation

module internal Placement =
    let key (window: LibTmux.Window) =
        let edge = window.Edge

        {
            ServerProcessId = window.Generation.ProcessId
            ServerStartTime = window.Generation.StartTime
            SessionId = edge.SessionId.Value
            WindowId = edge.WindowId.Value
            WindowIndex = edge.WindowIndex
        }
