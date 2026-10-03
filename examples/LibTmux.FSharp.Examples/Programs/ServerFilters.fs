// fsharp-snippet: ServerFilters
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
                SocketName = "fsharp-filters-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = options |> Server.createOwned token

        use! demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "jobs", Command = "/bin/sh"),
                token
            )

        let! server = options |> Server.connect token
        let! sessions = server |> Server.sessions |> Query.list token

        let nativeMatches =
            sessions
            |> Seq.filter (fun session -> session.Name.StartsWith("de", StringComparison.Ordinal))
            |> Seq.toList

        let portableMatches =
            sessions |> Query.matching (Filter.startsWith "de" SessionFields.name)

        let! windows = server |> Server.windows |> Query.list token

        let matchingWindows =
            windows |> Query.matching (Filter.eq "shell" WindowFields.name)

        let! captured = server |> Server.capture token SnapshotDepth.Panes

        let demoSession =
            captured.Sessions |> Seq.find (fun session -> session.Id = demo.Value.Id)

        let demoPane =
            demoSession.Windows
            |> Seq.collect (fun window -> window.Panes)
            |> Seq.exactlyOne

        let paneFilter = Filter.eq demoPane.Id PaneFields.id
        let matchingPanes = captured.Panes |> Query.matching paneFilter

        let hasDemoPane =
            paneFilter |> Filter.any WindowFields.panes |> Filter.any SessionFields.windows

        let matchingParents = captured.Sessions |> Query.matching hasDemoPane

        use! _control = server |> Control.enter token
        let! clients = server |> Server.clients |> Query.list token

        let controlClients =
            clients |> Query.matching (Filter.eq true ClientFields.controlMode)

        let names (sessions: seq<LibTmux.Session>) =
            [ for session in sessions -> session.Name ]

        printfn "native: %A" (names nativeMatches)
        printfn "portable: %A" (names portableMatches)
        printfn "windows: %A" [ for window in matchingWindows -> window.Name ]
        printfn "panes: %d" matchingPanes.Count
        printfn "parents: %A" (names matchingParents)
        printfn "control clients: %d" controlClients.Count
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
