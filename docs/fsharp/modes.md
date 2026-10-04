# Execution modes

The caller chooses how commands reach tmux: one tmux process per command, a
control client that stays attached, or a chain that runs several commands in
one tmux call.

Use one-shot operations when one task describes the work. Use a control client
when tmux must report work that nobody explicitly requested. Use the `Chain`
module when several commands should run together, each acting on what the one
before made.

## Control mode

`Control.withSession` opens and owns a core control client. It ends the client
after its task finishes. `readUntilTerminalAsync` borrows a client, so it
disposes only its event enumerator. `Control.enter` returns an owned client
when its lifetime must extend beyond one function, and `Control.enterSession`
attaches one to a chosen session; pass either to `Control.useSession` to
transfer ownership to a task scope.

<!-- fsharp-snippet: ObserveControlEvents run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let readUntilTerminalAsync
    (cancellationToken: CancellationToken)
    (session: IControlModeSession)
    =
    session
    |> Control.events
    |> Control.foldWhile
        cancellationToken
        (fun events event ->
            task {
                let retained = event :: events

                match event with
                | :? TmuxEventsDroppedEvent
                | :? TmuxExitEvent ->
                    return StreamStep.Stop(List.rev retained)
                | _ -> return StreamStep.Continue retained
            })
        []

let observeUntilTerminalAsync cancellationToken server =
    server
    |> Control.withSession
        cancellationToken
        (readUntilTerminalAsync cancellationToken)
```
<!-- endfsharp-snippet -->

`Control.events` is the client's event stream; nothing is read until a
consumer enumerates it. `Control.foldWhile` reads and awaits one folder call at
a time. It stops before reading another event when the folder returns
`StreamStep.Stop`.
It preserves unknown event types. `TmuxEventsDroppedEvent` means the caller
must resynchronize from a capture; when its `OnlyOutput` is true, only pane
output was lost and every notification arrived. The example returns it to the
caller and stops. `TmuxExitEvent` is a normal terminal event. A failed control stream
raises after its buffered events.

`Control.iter` is the same borrowed-client pattern when no accumulator is
needed. It completes when the stream ends or propagates the handler, stream,
and cancellation errors unchanged. When a handler and the enumerator's cleanup
both fail, the handler's exception propagates and `Control.cleanupFailure`
returns the cleanup's.


## Command chains

A chain is a core `TmuxChain`, which F# can build directly or through the
`Chain` module below. Building it makes no I/O; one
`ExecuteAsync` dispatches the commands in order and returns their combined
output.

<!-- fsharp-snippet: ChainCommands run -->
```fsharp run
open System.Threading
open LibTmux

let readChainOutputAsync
    (cancellationToken: CancellationToken)
    (server: Server)
    =
    task {
        // Each typed request becomes one command of the chain.
        let print text =
            DisplayMessageRequest(Format = text, ReturnText = true)
                .ToCommand(server)

        let chain =
            server
                .Chain()
                .Then(print "fsharp-chain-first")
                .Then(print "fsharp-chain-second")

        let! result = chain.ExecuteAsync(cancellationToken)
        return result.StandardOutputLines |> Seq.toList
    }
```
<!-- endfsharp-snippet -->

Forty request types, such as `SendKeysRequest`, `SplitPaneRequest` and
`CapturePaneRequest`, convert to a command with `ToCommand`, so a chain keeps
their validation; `Then(name, arguments)` takes any other command as text.
The `Chain` module builds the same chain as a pipeline, with named steps that
act on what the step before made: `Chain.newWindow`, `Chain.splitLeftRight`,
`Chain.splitTopBottom`, `Chain.sendLine` and `Chain.arrange`, then
`Chain.run`; `Chain.add` appends a typed request's command. The
[session program](getting-started.md#describe-a-session) uses one.
Use a chain for a known batch. It returns one `TmuxCommandResult`, not a typed
object for every step. Use one-shot operations when each step needs a refreshed
entity handle.

## Bounded concurrent reads

`Task.WhenAll` starts all inputs. Supply a bound when the input size can grow;
this helper holds at most `maximumConcurrency` pane captures at once and
returns results in input order.

<!-- fsharp-snippet: BoundedCapture run -->
```fsharp run
open System
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp

let boundedMapAsync
    maximumConcurrency
    (cancellationToken: CancellationToken)
    (work: CancellationToken -> 'Input -> Task<'Output>)
    (inputs: 'Input list)
    =
    if maximumConcurrency < 1 then
        invalidArg
            "maximumConcurrency"
            "Maximum concurrency must be positive."

    task {
        use gate = new SemaphoreSlim(maximumConcurrency)

        let run index input =
            task {
                do! gate.WaitAsync(cancellationToken)

                try
                    let! output = work cancellationToken input
                    return index, output
                finally
                    gate.Release() |> ignore
            }

        let! indexed = inputs |> List.mapi run |> Task.WhenAll

        return indexed |> Array.sortBy fst |> Array.map snd |> Array.toList
    }

let capturePanesBoundedAsync
    maximumConcurrency
    cancellationToken
    (panes: seq<Pane>)
    =
    panes
    |> Seq.toList
    |> boundedMapAsync
        maximumConcurrency
        cancellationToken
        (fun token pane ->
            pane |> Pane.capture token (CapturePaneRequest()))
```
<!-- endfsharp-snippet -->

Cancellation reaches waiting and active reads. The gate is released when an
operation succeeds, fails, or is canceled.
