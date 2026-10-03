// fsharp-snippet: Quickstart
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        // Each session's one window runs a plain shell.
        for name in [ "build"; "web"; "worker" ] do
            let shell =
                { SessionSpec.named name with
                    Windows =
                        [
                            { WindowSpec.empty with
                                Command = Some "/bin/sh"
                            }
                        ]
                }

            let! _ = owned.Value |> Server.newSession token shell
            ()

        let! server = LibTmux.Server.ConnectAsync(options, token)

        // List and filter: tmux narrows the listing, then every row is rechecked.
        // atMostOne is None when nothing matches and raises when several do.
        let! build =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "build")
            |> Query.atMostOne token

        let! others =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.ne "build")
            |> Query.list token

        printfn "other sessions: %s" (String.Join(", ", [ for session in others -> session.Name ]))

        match build with
        | None -> printfn "no build session"
        | Some session ->
            let! panes = session |> Session.panes |> Query.list token
            let pane = panes[0]

            // Type a command and wait for what it prints, not for its echo.
            let! started =
                pane
                |> Pane.sendAndWait token (TimeSpan.FromSeconds 10.) "echo build started" "build started"

            // Run a command to its exit status and read what it printed.
            let! result =
                pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

            printfn "wait found: %b" started.Found

            match result with
            | PaneRun.Exited status -> printfn "run: exit %d, output %A" status (List.ofSeq result.Output)
            | _ -> printfn "run: did not finish"
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
