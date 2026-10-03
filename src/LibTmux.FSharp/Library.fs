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
module Retry =
    let ifNotSent (cancellationToken: CancellationToken) (retries: int) (operation: CancellationToken -> Task<'T>) =
        if retries < 0 then
            raise (ArgumentOutOfRangeException(nameof retries, retries, "The retry count is negative."))

        backgroundTask {
            let mutable remaining = retries
            let mutable result = None

            while result.IsNone do
                // A NotSent failure clears only its own command; anything the
                // attempt sent before it may have run.
                let attempt = LibTmux.Internal.TmuxDispatchLedger.Create()

                try
                    let! value =
                        LibTmux.Internal.TmuxDispatchLedger.CountAsync(attempt, (fun () -> operation cancellationToken))

                    result <- Some value
                with TmuxFailure.NotSent _ when
                    remaining > 0
                    && not attempt.AnyReached
                    && not cancellationToken.IsCancellationRequested ->
                    remaining <- remaining - 1

            return result.Value
        }

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
