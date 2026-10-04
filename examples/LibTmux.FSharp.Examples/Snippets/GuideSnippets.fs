namespace LibTmux.FSharp.Examples

module internal GuideSnippets =
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readOwnedPaneCommandsAsync (cancellationToken: CancellationToken) =
        task {
            let options =
                ServerConnectionOptions(
                    SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                    ConfigurationFile = "/dev/null"
                )

            use! ownedServer = options |> Server.createOwned cancellationToken

            use! _ownedSession =
                ownedServer.Value.CreateOwnedSessionAsync(
                    NewSessionRequest(Name = "demo", Command = "/bin/sh"),
                    cancellationToken
                )

            let! captured =
                ownedServer.Value |> Server.capture cancellationToken SnapshotDepth.Panes

            return captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList
        }

    // fsharp-snippet: SendWaitList
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let runInShellAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            // List and filter: tmux narrows the listing, then every row is rechecked.
            let! shells =
                server
                |> Server.panes
                |> Query.where (PaneFields.currentCommand |> Filter.oneOf [ "bash"; "sh"; "zsh" ])
                |> Query.list cancellationToken

            match shells |> Seq.tryHead with
            | None -> return None
            | Some pane ->
                // Type a command and wait for what it prints, not for its echo.
                let! ready =
                    pane
                    |> Pane.sendAndWait cancellationToken (TimeSpan.FromSeconds 10.) "echo ready" "ready"

                // Run a command to its exit status and read what it printed.
                let! listing = pane |> Pane.run cancellationToken (TimeSpan.FromSeconds 30.) "ls /"

                match listing with
                | PaneRun.Exited status -> return Some(ready.Found, status, listing.Output)
                | PaneRun.Ended
                | PaneRun.NotStarted
                | PaneRun.TimedOut -> return None
        }
    // endfsharp-snippet

    // fsharp-snippet: QueryAtAGlance
    open System.Collections.Generic
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let queryShapesAsync (ct: CancellationToken) (server: Server) (session: Session) (held: Pane list) =
        task {
            // Describing a listing reads nothing.
            let named =
                server
                |> Server.sessions
                |> Query.where (SessionFields.name |> Filter.startsWith "bu")

            let! (all: IReadOnlyList<Session>) = named |> Query.list ct
            let! (one: Result<Session, CardinalityError>) = named |> Query.exactlyOne ct
            let! (maybe: Session option) = named |> Query.tryExactlyOne ct
            let! (atMost: Session option) = named |> Query.atMostOne ct
            let! (inSession: IReadOnlyList<Pane>) = session |> Session.panes |> Query.list ct

            let! (showingError: IReadOnlyList<Pane>) =
                server
                |> Server.panes
                |> Query.showing (ScreenSearch.Text "ERROR:")
                |> Query.list ct

            let! (withTail: IReadOnlyList<Session>) =
                server
                |> Server.sessions
                |> Query.where (WindowFields.name |> Filter.eq "tail" |> Filter.any SessionFields.windows)
                |> Query.list ct

            let! (active: IReadOnlyList<Pane>) =
                server
                |> Server.panes
                |> Query.whereUnsafe (UnsafeTmuxFilter "#{pane_active}")
                |> Query.list ct

            // Objects already in hand are filtered locally.
            let editors: IReadOnlyList<Pane> =
                held
                |> Query.matching (PaneFields.currentCommand |> Filter.oneOf [ "nvim"; "vim" ])

            return all, one, maybe, atMost, inSession, showingError, withTail, active, editors
        }
    // endfsharp-snippet

    // fsharp-snippet: FilterAtAGlance
    open System.Collections.Generic
    open LibTmux
    open LibTmux.FSharp

    let filterShapes (panes: Pane list) (capturedSessions: Session list) =
        // A filter is a value; building one makes no tmux call.
        let editor: Filter<Pane> =
            PaneFields.currentCommand |> Filter.oneOf [ "nvim"; "vim" ]

        // The same filter applies to objects already in hand.
        let editors: IReadOnlyList<Pane> = panes |> Query.matching editor
        let isEditor: Pane -> bool = Filter.toPredicate editor
        let firstEditor: Pane option = panes |> List.tryFind isEditor

        // A relation filter reads captured children; its document says how deep.
        let hasEditor: Filter<Session> =
            editor |> Filter.any WindowFields.panes |> Filter.any SessionFields.windows

        let depth: SnapshotDepth = (Filter.toDocument hasEditor).RequiredSnapshotDepth

        let withEditor: IReadOnlyList<Session> =
            capturedSessions |> Query.matching hasEditor

        editors, firstEditor, depth, withEditor
    // endfsharp-snippet

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
            let options =
                ServerConnectionOptions(
                    SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                    ConfigurationFile = "/dev/null"
                )

            use! ownedServer = options |> Server.createOwned cancellationToken
            let server = ownedServer.Value

            use! ownedSession =
                server.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), cancellationToken)

            let! panes = ownedSession.Value |> Session.panes |> Query.list cancellationToken

            let! second =
                panes[0] |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

            do! second |> Pane.sendLine cancellationToken "printf 'ready\\n'"

            let! found = server |> Server.tryFindPane cancellationToken second.Id
            let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

            return
                captured.Panes |> Seq.map (fun pane -> pane.Id) |> Seq.toList, found |> Option.map (fun pane -> pane.Id)
        }
    // endfsharp-snippet

    // fsharp-snippet: ObserveControlEvents
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readUntilTerminalAsync (cancellationToken: CancellationToken) (session: IControlModeSession) =
        session
        |> Control.events
        |> Control.foldWhile
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

    // fsharp-snippet: WatchPaneOutput
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readPaneUntilAsync
        (cancellationToken: CancellationToken)
        (marker: string)
        (pane: Pane)
        (session: IControlModeSession)
        =
        session
        |> Control.watchPane pane
        |> Control.foldWhile
            cancellationToken
            (fun output event ->
                task {
                    match event with
                    | PaneWatch.Output printed ->
                        let output = output + printed.Data

                        if output.Contains(marker, StringComparison.Ordinal) then
                            return StreamStep.Stop output
                        else
                            return StreamStep.Continue output
                    // Output tmux held back or the buffer dropped never arrives;
                    // capture the pane to read what the screen shows instead.
                    | PaneWatch.Paused _
                    | PaneWatch.Continued _
                    | PaneWatch.Dropped _ -> return StreamStep.Continue output
                    | PaneWatch.Gone _
                    | PaneWatch.Exited _ -> return StreamStep.Stop output
                })
            ""
    // endfsharp-snippet


    // fsharp-snippet: ChainCommands
    open System.Threading
    open LibTmux

    let readChainOutputAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            // Each typed request becomes one command of the chain.
            let print text =
                DisplayMessageRequest(Format = text, ReturnText = true).ToCommand(server)

            let chain =
                server.Chain().Then(print "fsharp-chain-first").Then(print "fsharp-chain-second")

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

    // fsharp-snippet: DescribeSession
    open System.Threading
    open LibTmux
    open LibTmux.Workspace

    let buildWorkspaceAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let description =
                WorkspaceFile(
                    sessionName = "build",
                    windows =
                        [
                            WorkspaceWindow(
                                windowName = "editor",
                                panes = [ WorkspacePane([ "printf 'editing\\n'" ]); WorkspacePane() ]
                            )
                            WorkspaceWindow(windowName = "logs", panes = [ WorkspacePane([ "printf 'tailing\\n'" ]) ])
                        ]
                )

            // Creates the session, its windows and panes, and sends each pane its
            // commands once its shell is ready.
            let! built = WorkspaceBuilder(server).BuildAsync(description, cancellationToken)
            return built.Session.Name, [ for window in built.Windows -> window.Name ]
        }
    // endfsharp-snippet

    // fsharp-snippet: TestWithScope
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp
    open LibTmux.Testing

    let greetingAsync (cancellationToken: CancellationToken) =
        task {
            // A private tmux server, session, window and pane, removed even if
            // the test fails.
            use! scope =
                TmuxTestFactory().CreateHierarchyAsync(cancellationToken = cancellationToken)

            let! shell =
                scope.Pane
                |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

            let! result =
                shell
                |> Pane.run cancellationToken (TimeSpan.FromSeconds 10.) "printf 'hello\\n'"

            return List.ofSeq result.Output
        }
    // endfsharp-snippet

    // fsharp-snippet: CiTestOptions
    open System
    open LibTmux
    open LibTmux.Testing

    // Options that name a connection replace the private socket a test gets
    // by default, so name a socket of the test's own as well as the binary.
    let testOptionsWith (tmuxBinary: string) =
        TmuxTestOptions(
            ServerConnectionOptions(
                SocketName = "libtmux-test-" + Guid.NewGuid().ToString("N"),
                TmuxBinaryPath = tmuxBinary
            )
        )
    // endfsharp-snippet

    // fsharp-snippet: SafeRetry
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readSessionNamesAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            try
                // Runs again only when tmux never received the command, after
                // each delay in turn, so a server still starting can answer.
                let! sessions =
                    Retry.ifNotSentAfter
                        cancellationToken
                        [ TimeSpan.FromMilliseconds 100.; TimeSpan.FromMilliseconds 400. ]
                        (fun token -> server.GetSessionsAsync(token))

                return Ok [ for session in sessions -> session.Name ]
            with
            | TmuxFailure.Ran failure -> return Error $"tmux ran the command, then: {failure.Message}"
            | TmuxFailure.MayHaveRun failure -> return Error $"tmux may have acted: {failure.Message}"
        }
    // endfsharp-snippet

    // fsharp-snippet: RunCancellation
    open System
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let runTestsAsync (cancellationToken: CancellationToken) (pane: Pane) =
        task {
            try
                let! result =
                    pane |> Pane.run cancellationToken (TimeSpan.FromMinutes 5.) "make test"

                match result with
                | PaneRun.Exited 0 -> return "passed"
                | PaneRun.Exited status -> return $"failed with status {status}"
                | PaneRun.Ended -> return "the shell exited before the tests finished"
                | PaneRun.NotStarted -> return "the shell was not at a prompt"
                | PaneRun.TimedOut -> return "still running after five minutes"
            with
            // Cancelled or lost once the command was sent: it may be running.
            // A tmux client cancelled mid-call matches too, even before the
            // command went, erring towards "may have run". That cancellation
            // is an OperationCanceledException, so this case comes first.
            | TmuxFailure.MayHaveRun _ -> return "may have run; read the pane before trying again"
            // Cancelled between tmux calls, before the command was sent.
            | :? OperationCanceledException -> return "cancelled before it was sent"
        }
    // endfsharp-snippet

    // fsharp-snippet: TypedOptions
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let tuneAsync (cancellationToken: CancellationToken) (session: Session) (window: Window) =
        task {
            do!
                session.Options
                |> Options.set cancellationToken TmuxOptionKey.HistoryLimit 50_000

            do!
                session.Options
                |> Options.set cancellationToken (TmuxOptionKey.Text "@stage") "build"

            let! history =
                session.Options |> Options.get cancellationToken TmuxOptionKey.HistoryLimit

            let! stage =
                session.Options |> Options.get cancellationToken (TmuxOptionKey.Text "@stage")

            // Never set on the window, so this is tmux's inherited default.
            let! renames =
                window.Options |> Options.get cancellationToken TmuxOptionKey.AutomaticRename

            return history, stage, renames
        }
    // endfsharp-snippet

    // fsharp-snippet: CoreInterop
    open System
    open System.Collections.Generic
    open System.Threading
    open LibTmux

    let inspectCoreSettingsAsync (cancellationToken: CancellationToken) (server: Server) (session: Session) =
        task {
            // A global value, overridden locally, shows through again once the
            // local value is unset.
            let! _ =
                session.Options.SetAsync(SetOptionRequest("status-keys", "vi", Global = true), cancellationToken)

            let! _ =
                session.Options.SetAsync(SetOptionRequest("status-keys", "emacs"), cancellationToken)

            do! session.Options.UnsetAsync(UnsetOptionRequest("status-keys"), cancellationToken)

            let! statusKeys =
                session.Options.GetAsync(GetOptionRequest("status-keys", IncludeInherited = true), cancellationToken)

            // An array option and a hook keep each entry's index.
            let! _ =
                server.Options.SetAsync(
                    SetOptionRequest("command-alias[40]", "fsharp-window=new-window"),
                    cancellationToken
                )

            let! aliases =
                server.Options.GetAsync(GetOptionRequest("command-alias"), cancellationToken)

            let entries = Dictionary<int, string>()
            entries[3] <- "display-message fsharp-hook"

            let! hook =
                server.Hooks.SetAsync(SetHooksRequest("alert-bell", entries, ClearExisting = true), cancellationToken)

            let! _ =
                session.Environment.SetAsync("LIBTMUX_FSHARP_EXAMPLE", "ready", cancellationToken = cancellationToken)

            let! variable =
                session.Environment.GetAsync("LIBTMUX_FSHARP_EXAMPLE", cancellationToken)

            let! rendered =
                server.DisplayMessageAsync(
                    DisplayMessageRequest(Format = "fsharp-#{pid}", ReturnText = true),
                    cancellationToken
                )

            return
                {|
                    StatusKeys = [ for option in statusKeys -> option.Value.Raw, option.Inherited ]
                    Alias =
                        aliases
                        |> Seq.tryFind (fun alias -> alias.Index = Nullable 40)
                        |> Option.map (fun alias -> alias.Value.Raw)
                    HookIndexes = [ for value in hook.Values -> value.Index ]
                    Variable = variable |> Option.ofObj |> Option.map (fun entry -> entry.Value)
                    Rendered = rendered |> Option.ofObj |> Option.map List.ofSeq
                |}
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
            // Link the window at index 5 as well, move that placement to 3,
            // then unlink it; the original placement stays.
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

            do! moved.UnlinkAsync(cancellationToken = cancellationToken)
            let! remaining = session.GetWindowsAsync(cancellationToken)

            // Layout and resize return the handle they changed.
            let! split = window.SplitPaneAsync(cancellationToken = cancellationToken)

            let! laidOut =
                window.SelectLayoutAsync(SelectLayoutRequest(Layout = "even-horizontal"), cancellationToken)

            let! resized = split.ResizeAsync(ResizePaneRequest(Height = "10"), cancellationToken)

            do! server.Buffers.SetAsync("fsharp-ready", "fsharp-guide", cancellationToken = cancellationToken)
            let! contents = server.Buffers.GetAsync("fsharp-guide", cancellationToken)
            do! server.Buffers.DeleteAsync("fsharp-guide", cancellationToken)

            do! pane.EnterCopyModeAsync(cancellationToken = cancellationToken)
            let! copying = pane.RefreshAsync(cancellationToken)
            do! pane.EnterCopyModeAsync(CopyModeRequest(Cancel = true), cancellationToken)
            let! normal = pane.RefreshAsync(cancellationToken)

            return
                {|
                    Moved = moved
                    Remaining = remaining
                    Split = split
                    LaidOut = laidOut
                    Resized = resized
                    Buffer = contents
                    InModeWhileCopying = copying.RawFormatFields["pane_in_mode"]
                    InModeAfter = normal.RawFormatFields["pane_in_mode"]
                |}
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
            let pane = panes[0]

            // Literal text is typed as written; a key name is pressed. Text
            // followed by Enter is two commands: the text, then the key.
            let literal = SendKeysRequest(Text = "Enter", Literal = true, Enter = false)
            let keyName = SendKeysRequest(Text = "Enter", Literal = false, Enter = false)

            let textThenEnter =
                SendKeysRequest(Text = "fsharp-input", Literal = true, Enter = true)

            do! pane |> Pane.sendKeys cancellationToken literal
            do! pane |> Pane.sendKeys cancellationToken keyName
            do! pane |> Pane.sendKeys cancellationToken textThenEnter
            do! window |> Window.kill cancellationToken

            let arguments (request: SendKeysRequest) =
                [
                    for command in request.ToCommands(pane) -> List.ofSeq (command.ToArguments())
                ]

            return
                {|
                    Window = window
                    Panes = panes.Count
                    Literal = arguments literal
                    KeyName = arguments keyName
                    TextThenEnter = arguments textThenEnter
                |}
        }
    // endfsharp-snippet
