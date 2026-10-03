open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        // LIBTMUX_TMUX picks the tmux CI is testing; without it, the tmux on PATH.
        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-create-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = options |> Server.createOwned token

        use! session =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/cat"),
                token
            )

        use! window =
            session.Value.CreateOwnedWindowAsync(NewWindowRequest(Name = "editor", Command = "/bin/cat"), token)

        let! panes = window.Value.GetPanesAsync(token)
        let original = panes |> Seq.exactlyOne

        let! added =
            original
            |> Pane.split token (SplitPaneRequest(Direction = PaneDirection.Right, Command = "/bin/cat"))

        let! server = options |> Server.connect token
        let! windows = server |> Server.windows |> Query.list token
        let! allPanes = server |> Server.panes |> Query.list token

        printfn "Created session demo and window editor."
        printfn "Windows: %d; panes: %d" windows.Count allPanes.Count
        printfn "The split made a new pane: %b" (added.Id <> original.Id)
    }

runAsync().GetAwaiter().GetResult()
