# Execution modes

The F# companion forwards task-based core operations. The caller chooses the
subprocess, control client, or command chain.

Use one-shot core operations when one task describes the work. Use a control
client when tmux must report work that nobody explicitly requested. A command
chain remains an explicit core value.

## Control mode

`Control.withSession` opens and owns a core control client. It ends the client
after its task finishes. `readUntilTerminalAsync` borrows a client, so it
disposes only its event enumerator. `Control.enter` returns an owned client
when its lifetime must extend beyond one function; pass that client to
`Control.useSession` to transfer ownership to a task scope.

<!-- fsharp-snippet: ObserveControlEvents run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let readUntilTerminalAsync (cancellationToken: CancellationToken) (session: IControlModeSession) =
    session
    |> Control.foldEventsWhile
        cancellationToken
        (fun events event ->
            task {
                let retained = event :: events

                match event with
                | :? TmuxEventsDroppedEvent
                | :? TmuxExitEvent -> return StreamStep.Stop(List.rev retained)
                | _ -> return StreamStep.Continue retained
            })
        []

let observeUntilTerminalAsync cancellationToken server =
    server
    |> Control.withSession cancellationToken (readUntilTerminalAsync cancellationToken)
```
<!-- endfsharp-snippet -->

`Control.foldEventsWhile` reads and awaits one event handler at a time. It
stops before reading another event when the folder returns `StreamStep.Stop`.
It preserves unknown event types. `TmuxEventsDroppedEvent` means the caller
must resynchronize from a capture; the example returns it to the caller and
stops. `TmuxExitEvent` is a normal terminal event. A failed control stream
raises after its buffered events.

`Control.iterEvents` is the same borrowed-client pattern when no accumulator
is needed. It completes when the stream ends or propagates the handler,
stream, and cancellation errors unchanged.
