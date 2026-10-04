open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-create-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        use! session =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/cat"),
                token
            )

        use! window =
            session.Value.CreateOwnedWindowAsync(
                NewWindowRequest(Name = "editor", Command = "/bin/cat", Attach = false),
                token
            )

        let! panes = window.Value.GetPanesAsync(token)
        let original = panes |> Seq.exactlyOne

        let! added =
            original
            |> Pane.split token (SplitPaneRequest(Direction = PaneDirection.Right, Command = "/bin/cat"))

        // Renaming and selecting return a handle carrying the state afterwards.
        let! notes = window.Value |> Window.rename token "notes"
        let wasCurrent = notes.Active
        let! current = notes |> Window.select token
        let! focused = original |> Pane.select token
        let! _ = current |> Window.selectLayout token "even-horizontal"
        let! titled = focused |> Pane.setTitle token "editor"
        do! added |> Pane.kill token

        let server = owned.Value
        let! windows = server |> Server.windows |> Query.list token
        let! allPanes = server |> Server.panes |> Query.list token

        printfn "Created session demo and window editor."
        printfn "The split made a new pane: %b" (added.Id <> original.Id)
        printfn "Renamed to %s; current before select: %b, after: %b" current.Name wasCurrent current.Active
        printfn "First pane active again: %b; titled %s" focused.Active titled.Title
        printfn "Windows: %d; panes after the kill: %d" windows.Count allPanes.Count
    }

runAsync().GetAwaiter().GetResult()
