// fsharp-snippet: Quickstart
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let token = deadline.Token

        // A server of its own on a private socket, without user configuration.
        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        // One session whose window runs a plain shell.
        let! session =
            owned.Value.CreateSessionAsync(NewSessionRequest(Name = "build", Command = "/bin/sh"), token)

        let! pane = session |> Session.activePane token

        // Type a command and wait for what it prints, not for its echo.
        let! started =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 10.) "echo build started" "build started"

        printfn "wait found: %b" started.Found

        // Run a command to its exit status and read what it printed.
        let! result =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

        match result with
        | PaneRun.Exited status -> printfn "run: exit %d, output %A" status (List.ofSeq result.Output)
        | _ -> printfn "run: did not finish"

        // List and filter: tmux narrows the listing, then every row is rechecked.
        let! found =
            owned.Value
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")
            |> Query.list token

        printfn "sessions: %s" (String.Join(", ", [ for listed in found -> listed.Name ]))
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
