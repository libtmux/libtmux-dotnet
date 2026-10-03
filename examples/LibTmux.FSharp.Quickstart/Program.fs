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

        for name in [ "build"; "web"; "worker" ] do
            let! _ =
                owned.Value.CreateSessionAsync(NewSessionRequest(Name = name, Command = "/bin/sh"), token)

            ()

        let! server = LibTmux.Server.ConnectAsync(options, token)

        // List and filter: tmux narrows the listing, then every row is rechecked.
        let! build =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "build")
            |> Query.exactlyOne token

        let! others =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.ne "build")
            |> Query.list token

        let session =
            build
            |> Result.defaultWith (fun error -> failwithf "Expected one build session: %A" error)

        let! panes = session |> Session.panes |> Query.list token
        let pane = panes[0]

        // Type a command and wait for what it prints, not for its echo.
        let! started =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 10.) "echo build started" "build started"

        // Run a command to its exit status and read what it printed.
        let! result =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

        printfn "other sessions: %s" (String.Join(", ", [ for session in others -> session.Name ]))
        printfn "wait found: %b" started.Found
        printfn "run: exit %d, output %A" result.ExitStatus.Value (List.ofSeq result.Output)
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
