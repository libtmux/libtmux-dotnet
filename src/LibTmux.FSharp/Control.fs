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
        task {
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

module private EventReader =
    let consume
        (cancellationToken: CancellationToken)
        (session: IControlModeSession)
        (work: IAsyncEnumerator<TmuxEvent> -> Task<'T>)
        =
        let reader = session.Events.GetAsyncEnumerator(cancellationToken)

        AsyncCleanup.run "LibTmux.ControlModeEventCleanupFailure" (fun () -> work reader) (fun () ->
            reader.DisposeAsync().AsTask())

[<RequireQualifiedAccess>]
module Control =
    let enter (cancellationToken: CancellationToken) (server: LibTmux.Server) =
        server.EnterControlModeAsync(cancellationToken = cancellationToken)

    let useSession (work: IControlModeSession -> Task<'State>) (session: IControlModeSession) =
        AsyncCleanup.run "LibTmux.ControlModeClientCleanupFailure" (fun () -> work session) (fun () ->
            session.DisposeAsync().AsTask())

    let withSession
        (cancellationToken: CancellationToken)
        (work: IControlModeSession -> Task<'State>)
        (server: LibTmux.Server)
        =
        task {
            let! session = enter cancellationToken server
            return! useSession work session
        }

    let iterEvents (cancellationToken: CancellationToken) (handler: TmuxEvent -> Task) (session: IControlModeSession) =
        EventReader.consume cancellationToken session (fun reader ->
            task {
                let mutable reading = true

                while reading do
                    let! next = reader.MoveNextAsync().AsTask()

                    if next then
                        do! handler reader.Current
                    else
                        reading <- false
            })

    let foldEventsWhile
        (cancellationToken: CancellationToken)
        (folder: 'State -> TmuxEvent -> Task<StreamStep<'State>>)
        (initial: 'State)
        (session: IControlModeSession)
        =
        EventReader.consume cancellationToken session (fun reader ->
            task {
                let mutable state = initial
                let mutable reading = true

                while reading do
                    let! next = reader.MoveNextAsync().AsTask()

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
