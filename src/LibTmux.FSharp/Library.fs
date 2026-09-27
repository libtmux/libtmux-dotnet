namespace LibTmux.FSharp

open System.Collections.Generic
open System.Threading
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
module Window =
    let placementKey (window: LibTmux.Window) =
        let edge = window.Edge

        {
            ServerProcessId = window.Generation.ProcessId
            ServerStartTime = window.Generation.StartTime
            SessionId = edge.SessionId.Value
            WindowId = edge.WindowId.Value
            WindowIndex = edge.WindowIndex
        }

[<RequireQualifiedAccess>]
module Server =
    let listPanes (cancellationToken: CancellationToken) (server: LibTmux.Server) =
        server.GetPanesAsync(cancellationToken)

    let capture (cancellationToken: CancellationToken) depth (server: LibTmux.Server) =
        server.CaptureSnapshotAsync(depth, cancellationToken)

    let tryFindPane (cancellationToken: CancellationToken) id (server: LibTmux.Server) =
        task {
            let! pane = server.FindPaneAsync(id, cancellationToken)
            return Option.ofObj pane
        }

[<RequireQualifiedAccess>]
module Pane =
    let currentPath (pane: LibTmux.Pane) = Option.ofObj pane.CurrentPath
    let currentCommand (pane: LibTmux.Pane) = Option.ofObj pane.CurrentCommand

    let capture (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.CaptureAsync(request, cancellationToken)

    let sendKeys (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SendKeysAsync(request, cancellationToken)

    let split (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SplitAsync(request, cancellationToken)
