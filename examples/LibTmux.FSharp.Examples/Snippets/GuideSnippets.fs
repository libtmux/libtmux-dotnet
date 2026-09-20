namespace LibTmux.FSharp.Examples

module internal GuideSnippets =
    // fsharp-snippet: CaptureAndFilter
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readMatchingSessionNamesAsync (commands: string list) (cancellationToken: CancellationToken) (server: Server) =
        task {
            let hasCommand =
                Filter.oneOf commands PaneFields.currentCommand
                |> Filter.any WindowFields.panes
                |> Filter.any SessionFields.windows

            let document = Filter.toDocument hasCommand

            let! captured =
                server |> Server.capture cancellationToken document.RequiredSnapshotDepth

            return
                captured.Sessions
                |> Query.matching hasCommand
                |> Seq.map (fun session -> session.Name)
                |> Seq.toList
        }

    let readEditorSessionNamesAsync cancellationToken server =
        readMatchingSessionNamesAsync [ "nvim"; "vim" ] cancellationToken server
    // endfsharp-snippet

    // fsharp-snippet: NativeQuery
    open System
    open LibTmux
    open LibTmux.FSharp

    let editorPaneIds (captured: Server) =
        captured.Panes
        |> Seq.filter (fun pane -> String.Equals(pane.CurrentCommand, "nvim", StringComparison.Ordinal))
        |> Seq.map (fun pane -> pane.Id)
        |> Seq.toList
    // endfsharp-snippet

    // fsharp-snippet: PortableFilter
    open LibTmux
    open LibTmux.FSharp

    let editor = Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand

    let acceptsEditor = editor |> Filter.toPredicate

    let firstEditor (captured: Server) =
        captured.Panes |> Seq.filter acceptsEditor |> Seq.tryHead
    // endfsharp-snippet

    // fsharp-snippet: LookupAndCardinality
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let tryFindPaneAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let! pane = server |> Server.tryFindPane cancellationToken (PaneId 42)

            return pane
        }

    let selectCapturedPane (captured: Server) = captured.Panes |> Selection.exactlyOne
    // endfsharp-snippet

    // fsharp-snippet: ReadPaneCommands
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readPaneCommandsAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

            return captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList
        }
    // endfsharp-snippet

    // fsharp-snippet: MatchingSessions
    open LibTmux
    open LibTmux.FSharp

    let editorSessions =
        Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
        |> Filter.any WindowFields.panes
        |> Filter.any SessionFields.windows

    let matchingSessions (captured: Server) =
        captured.Sessions |> Query.matching editorSessions
    // endfsharp-snippet

    // fsharp-snippet: ObserveControlEvents
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
    // endfsharp-snippet


    // fsharp-snippet: ChainCommands
    open System.Threading
    open LibTmux

    let readChainOutputAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let chain =
                server
                    .Chain()
                    .Then("display-message", "-p", "fsharp-chain-first")
                    .Then("display-message", "-p", "fsharp-chain-second")

            let! result = chain.ExecuteAsync(cancellationToken)
            return result.StandardOutputLines |> Seq.toList
        }
    // endfsharp-snippet

    // fsharp-snippet: BoundedCapture
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
            invalidArg "maximumConcurrency" "Maximum concurrency must be positive."

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

    let capturePanesBoundedAsync maximumConcurrency cancellationToken (panes: seq<Pane>) =
        panes
        |> Seq.toList
        |> boundedMapAsync maximumConcurrency cancellationToken (fun token pane ->
            pane |> Pane.capture token (CapturePaneRequest()))
    // endfsharp-snippet

    // fsharp-snippet: PortableFilterJson
    open LibTmux
    open LibTmux.FSharp
    open LibTmux.Query.Json

    let encodeEditorPaneFilter () =
        Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
        |> Filter.toDocument
        |> QueryJson.Serialize

    let decodeFilter json = QueryJson.Deserialize json
    // endfsharp-snippet
