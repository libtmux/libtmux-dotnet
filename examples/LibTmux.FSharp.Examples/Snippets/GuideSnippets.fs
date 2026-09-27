namespace LibTmux.FSharp.Examples

module internal GuideSnippets =
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readOwnedPaneCommandsAsync (cancellationToken: CancellationToken) =
        task {
            let binary =
                Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
                |> Option.ofObj
                |> Option.defaultValue "tmux"

            let options =
                ServerConnectionOptions(
                    SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                    ConfigurationFile = "/dev/null",
                    TmuxBinaryPath = binary
                )

            use! ownedServer = LibTmux.Server.CreateOwnedAsync(options, cancellationToken)

            use! _ownedSession =
                ownedServer.Value.CreateOwnedSessionAsync(
                    NewSessionRequest(Name = "demo", Command = "/bin/sh"),
                    cancellationToken
                )

            let! captured =
                ownedServer.Value |> Server.capture cancellationToken SnapshotDepth.Panes

            return captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList
        }

    // fsharp-snippet: RelationFilter
    open LibTmux.FSharp

    let sessionsWithCommands commands =
        Filter.oneOf commands PaneFields.currentCommand
        |> Filter.any WindowFields.panes
        |> Filter.any SessionFields.windows

    let editorFilter = sessionsWithCommands [ "nvim"; "vim" ]

    printfn "capture depth: %A" (Filter.toDocument editorFilter).RequiredSnapshotDepth
    // endfsharp-snippet

    let readMatchingSessionNamesAsync (commands: string list) (cancellationToken: CancellationToken) (server: Server) =
        task {
            let hasCommand = sessionsWithCommands commands

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

    // fsharp-snippet: OwnedWorkflow
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let inspectOwnedSessionAsync (cancellationToken: CancellationToken) =
        task {
            let binary =
                Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
                |> Option.ofObj
                |> Option.defaultValue "tmux"

            let options =
                ServerConnectionOptions(
                    SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                    ConfigurationFile = "/dev/null",
                    TmuxBinaryPath = binary
                )

            use! ownedServer = LibTmux.Server.CreateOwnedAsync(options, cancellationToken)
            let server = ownedServer.Value

            use! ownedSession =
                server.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), cancellationToken)

            let! sessions = server.GetSessionsAsync(cancellationToken)
            let! clients = server.GetClientsAsync(cancellationToken)
            let! foundSession = server.FindSessionAsync(ownedSession.Value.Id, cancellationToken)

            if
                sessions.Count <> 1
                || sessions[0].Id <> ownedSession.Value.Id
                || clients.Count <> 0
                || isNull foundSession
            then
                failwith "The owned session was not discoverable on its detached server."

            let! panes = server |> Server.listPanes cancellationToken

            let first =
                panes
                |> Selection.exactlyOne
                |> Result.defaultWith (fun error -> failwithf "Expected one initial pane: %A" error)

            let! second =
                first |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

            do!
                second
                |> Pane.sendKeys cancellationToken (SendKeysRequest(Text = "printf 'ready\\n'", Literal = true))

            let! found = server |> Server.tryFindPane cancellationToken second.Id
            let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

            return
                captured.Panes |> Seq.map (fun pane -> pane.Id) |> Seq.toList, found |> Option.map (fun pane -> pane.Id)
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

    // fsharp-snippet: CoreInterop
    open System
    open System.Collections.Generic
    open System.Threading
    open LibTmux

    let inspectCoreSettingsAsync (cancellationToken: CancellationToken) (server: Server) (session: Session) =
        task {
            let! _ =
                session.Options.SetAsync(SetOptionRequest("status-keys", "vi", Global = true), cancellationToken)

            let! _ =
                session.Options.SetAsync(SetOptionRequest("status-keys", "emacs"), cancellationToken)

            do! session.Options.UnsetAsync(UnsetOptionRequest("status-keys"), cancellationToken)

            let! inherited =
                session.Options.GetAsync(GetOptionRequest("status-keys", IncludeInherited = true), cancellationToken)

            if
                inherited.Count <> 1
                || inherited[0].Value.Raw <> "vi"
                || not inherited[0].Inherited
            then
                failwith "Unsetting the local option did not restore the inherited value."

            let! _ =
                server.Options.SetAsync(
                    SetOptionRequest("command-alias[40]", "fsharp-window=new-window"),
                    cancellationToken
                )

            let! aliases =
                server.Options.GetAsync(GetOptionRequest("command-alias"), cancellationToken)

            let indexed =
                aliases
                |> Seq.tryFind (fun alias -> alias.Index = Nullable 40)
                |> Option.defaultWith (fun () -> failwith "The indexed option was not reported.")

            if indexed.Value.Raw <> "fsharp-window=new-window" then
                failwith "The indexed option lost its raw value."

            let entries = Dictionary<int, string>()
            entries[3] <- "display-message fsharp-hook"

            let! hook =
                server.Hooks.SetAsync(SetHooksRequest("alert-bell", entries, ClearExisting = true), cancellationToken)

            if hook.Values.Count <> 1 || hook.Values[0].Index <> 3 then
                failwith "The hook lost its command index."

            let! stored =
                session.Environment.SetAsync("LIBTMUX_FSHARP_EXAMPLE", "ready", cancellationToken = cancellationToken)

            let! observed =
                session.Environment.GetAsync("LIBTMUX_FSHARP_EXAMPLE", cancellationToken)

            let observedValue =
                match observed with
                | null -> failwith "The session environment value was absent."
                | entry -> entry.Value

            if stored.Value <> "ready" || observedValue <> "ready" then
                failwith "The session environment did not retain its value."

            let! rendered =
                server.DisplayMessageAsync(
                    DisplayMessageRequest(Format = "fsharp-#{pid}", ReturnText = true),
                    cancellationToken
                )

            let formatText =
                match rendered with
                | null -> failwith "tmux returned no format text."
                | lines when lines.Count = 1 -> lines[0]
                | _ -> failwith "tmux returned more than one format line."

            if not (formatText.StartsWith("fsharp-", StringComparison.Ordinal)) then
                failwith "tmux did not render the format."

            return indexed.Index.Value, inherited[0].Value.Raw, hook.Values[0].Index, formatText
        }
    // endfsharp-snippet

    // fsharp-snippet: CoreOperations
    open System.Threading
    open LibTmux

    let exerciseCoreOperationsAsync
        (cancellationToken: CancellationToken)
        (server: Server)
        (session: Session)
        (window: Window)
        (pane: Pane)
        =
        task {
            do!
                window.LinkAsync(
                    LinkWindowRequest(session.Id.ToString(), TargetIndex = "5", Detach = true),
                    cancellationToken
                )

            let! placements = session.GetWindowsAsync(cancellationToken)

            let linked =
                placements |> Seq.find (fun item -> item.Id = window.Id && item.Index = 5)

            let! moved =
                linked.MoveAsync(MoveWindowRequest(Destination = "3", NoSelect = true), cancellationToken)

            if moved.Id <> window.Id || moved.Index <> 3 then
                failwith "Moving the link did not return its new placement."

            do! moved.UnlinkAsync(cancellationToken = cancellationToken)
            let! remaining = session.GetWindowsAsync(cancellationToken)

            if remaining.Count <> 1 || remaining[0].Id <> window.Id then
                failwith "Unlinking the moved placement also removed the original."

            let! split = window.SplitPaneAsync(cancellationToken = cancellationToken)

            let! laidOut =
                window.SelectLayoutAsync(SelectLayoutRequest(Layout = "even-horizontal"), cancellationToken)

            let! resized = split.ResizeAsync(ResizePaneRequest(Height = "10"), cancellationToken)

            if laidOut.Id <> window.Id || resized.Id <> split.Id || resized.Height < 1 then
                failwith "Layout or resize did not return the affected handle."

            do! server.Buffers.SetAsync("fsharp-ready", "fsharp-guide", cancellationToken = cancellationToken)
            let! contents = server.Buffers.GetAsync("fsharp-guide", cancellationToken)

            if contents <> "fsharp-ready" then
                failwith "The named buffer lost its contents."

            do! server.Buffers.DeleteAsync("fsharp-guide", cancellationToken)
            do! pane.EnterCopyModeAsync(cancellationToken = cancellationToken)
            let! copying = pane.RefreshAsync(cancellationToken)

            if copying.RawFormatFields["pane_in_mode"] <> "1" then
                failwith "The pane did not enter copy mode."

            do! pane.EnterCopyModeAsync(CopyModeRequest(Cancel = true), cancellationToken)
            let! normal = pane.RefreshAsync(cancellationToken)

            if normal.RawFormatFields["pane_in_mode"] <> "0" then
                failwith "The pane did not leave copy mode."
        }
    // endfsharp-snippet

    // fsharp-snippet: WindowInput
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let exerciseWindowInputAsync (cancellationToken: CancellationToken) (session: Session) =
        task {
            let! window =
                session.CreateWindowAsync(
                    NewWindowRequest(Name = "fsharp-input", Command = "/bin/cat", Attach = false),
                    cancellationToken
                )

            let! panes = window.GetPanesAsync(cancellationToken)

            if window.Name <> "fsharp-input" || panes.Count <> 1 then
                failwith "The new window did not contain one pane."

            let pane = panes[0]
            let literal = SendKeysRequest(Text = "Enter", Literal = true, Enter = false)
            let keyName = SendKeysRequest(Text = "Enter", Literal = false, Enter = false)

            let textThenEnter =
                SendKeysRequest(Text = "fsharp-input", Literal = true, Enter = true)

            let literalCommands = literal.ToCommands(pane)
            let keyCommands = keyName.ToCommands(pane)
            let textCommands = textThenEnter.ToCommands(pane)

            let hasLiteral (command: TmuxCommand) =
                command.ToArguments() |> Seq.contains "-l"

            if
                literalCommands.Count <> 1
                || not (hasLiteral literalCommands[0])
                || keyCommands.Count <> 1
                || hasLiteral keyCommands[0]
                || textCommands.Count <> 2
                || not (hasLiteral textCommands[0])
                || hasLiteral textCommands[1]
                || (textCommands[1].ToArguments() |> Seq.last) <> "Enter"
            then
                failwith "Literal text and the Enter key were not separate commands."

            do! pane |> Pane.sendKeys cancellationToken literal
            do! pane |> Pane.sendKeys cancellationToken keyName
            do! pane |> Pane.sendKeys cancellationToken textThenEnter
            do! window.KillAsync(cancellationToken = cancellationToken)
        }
    // endfsharp-snippet
