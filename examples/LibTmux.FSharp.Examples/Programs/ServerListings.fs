// fsharp-snippet: ServerListings
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-listings-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! _demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "jobs", Command = "/bin/sh"),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! sessions = server |> Server.listSessions token
        let! windows = server |> Server.listWindows token
        let! panes = server |> Server.listPanes token
        let! clients = server |> Server.listClients token

        for session in sessions do
            printfn "Session: %s (%O)" session.Name session.Id

        printfn "Windows: %d; panes: %d; clients: %d" windows.Count panes.Count clients.Count

        if
            sessions.Count <> 2
            || windows.Count <> 2
            || panes.Count <> 2
            || clients.Count <> 0
        then
            failwith "Expected two detached sessions, each with one window and pane."
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
