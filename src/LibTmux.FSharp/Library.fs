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
    let (|NotSent|_|) (error: exn) =
        match error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.NotDispatched -> Some failure
        | _ -> None

    let (|Ran|_|) (error: exn) =
        match error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.Dispatched -> Some failure
        | _ -> None

    let (|MayHaveRun|_|) (error: exn) =
        match error with
        | :? LibTmuxException as failure when failure.Dispatch = TmuxDispatchState.Unknown -> Some error
        | :? TmuxOperationCanceledException as canceled when canceled.CommandMayHaveExecuted -> Some error
        | _ -> None

[<RequireQualifiedAccess>]
module PaneRun =
    let (|Exited|_|) (result: PaneRunResult) = Option.ofNullable result.ExitStatus

    let (|TimedOut|_|) (result: PaneRunResult) =
        if result.TimedOut then Some() else None

    let (|NotStarted|_|) (result: PaneRunResult) = if result.Started then None else Some()

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
