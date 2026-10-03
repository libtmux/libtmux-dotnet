namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open LibTmux

[<RequireQualifiedAccess>]
type StreamStep<'State> =
    | Continue of state: 'State
    | Stop of state: 'State

module private AsyncCleanup =
    let run (cleanupKey: string) (work: unit -> Task<'T>) (cleanup: unit -> Task) =
        backgroundTask {
            let mutable outcome: Result<'T, exn> option = None

            try
                let! result = work ()
                outcome <- Some(Ok result)
            with error ->
                outcome <- Some(Error error)

            let mutable cleanupFailure: exn option = None

            try
                do! cleanup ()
            with error ->
                cleanupFailure <- Some error

            match outcome, cleanupFailure with
            | Some(Ok result), None -> return result
            | Some(Ok _), Some cleanup ->
                ExceptionDispatchInfo.Capture(cleanup).Throw()
                return Unchecked.defaultof<'T>
            | Some(Error primary), None ->
                ExceptionDispatchInfo.Capture(primary).Throw()
                return Unchecked.defaultof<'T>
            | Some(Error primary), Some cleanup ->
                primary.Data[cleanupKey] <- cleanup
                ExceptionDispatchInfo.Capture(primary).Throw()
                return Unchecked.defaultof<'T>
            | None, _ -> return invalidOp "The asynchronous resource scope did not finish its work."
        }

[<RequireQualifiedAccess>]
module Control =
    [<Literal>]
    let private CleanupFailureKey = "LibTmux.FSharp.CleanupFailure"

    let enter (cancellationToken: CancellationToken) (server: LibTmux.Server) =
        server.EnterControlModeAsync(cancellationToken = cancellationToken)

    let enterSession (cancellationToken: CancellationToken) (session: LibTmux.Session) =
        session.Server.EnterControlModeAsync(session.Id.ToString(), cancellationToken)

    let useSession (work: IControlModeSession -> Task<'State>) (session: IControlModeSession) =
        AsyncCleanup.run CleanupFailureKey (fun () -> work session) (fun () -> session.DisposeAsync().AsTask())

    let withSession
        (cancellationToken: CancellationToken)
        (work: IControlModeSession -> Task<'State>)
        (server: LibTmux.Server)
        =
        backgroundTask {
            let! session = enter cancellationToken server
            return! useSession work session
        }

    let events (session: IControlModeSession) = session.Events

    let watchPane (pane: LibTmux.Pane) (session: IControlModeSession) =
        PaneObservation.WatchAsync(session, pane)

    let private consume
        (cancellationToken: CancellationToken)
        (source: IAsyncEnumerable<'T>)
        (work: IAsyncEnumerator<'T> -> Task<'Result>)
        =
        let reader = source.GetAsyncEnumerator(cancellationToken)

        AsyncCleanup.run CleanupFailureKey (fun () -> work reader) (fun () -> reader.DisposeAsync().AsTask())

    let iter (cancellationToken: CancellationToken) (handler: 'T -> Task) (source: IAsyncEnumerable<'T>) =
        consume cancellationToken source (fun reader ->
            backgroundTask {
                let mutable reading = true

                while reading do
                    let! next = reader.MoveNextAsync()

                    if next then
                        do! handler reader.Current
                    else
                        reading <- false
            })

    let foldWhile
        (cancellationToken: CancellationToken)
        (folder: 'State -> 'T -> Task<StreamStep<'State>>)
        (initial: 'State)
        (source: IAsyncEnumerable<'T>)
        =
        consume cancellationToken source (fun reader ->
            backgroundTask {
                let mutable state = initial
                let mutable reading = true

                while reading do
                    let! next = reader.MoveNextAsync()

                    if next then
                        let! step = folder state reader.Current

                        match step with
                        | StreamStep.Continue nextState -> state <- nextState
                        | StreamStep.Stop nextState ->
                            state <- nextState
                            reading <- false
                    else
                        reading <- false

                return state
            })

    let cleanupFailure (error: exn) =
        match error.Data[CleanupFailureKey] with
        | :? exn as cleanup -> Some cleanup
        | _ -> None
