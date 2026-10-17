// fsharp-snippet: ServerListings
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
                SocketName = "fsharp-listings-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        use! _demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(
                    Name = "demo",
                    WindowName = "shell",
                    Command = "/bin/sh"
                ),
                token
            )

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(
                    Name = "worker",
                    WindowName = "jobs",
                    Command = "/bin/sh"
                ),
                token
            )

        let server = owned.Value
        let! sessions = server |> Server.sessions |> Query.list token
        let! windows = server |> Server.windows |> Query.list token
        let! panes = server |> Server.panes |> Query.list token
        let! clients = server |> Server.clients |> Query.list token

        for session in sessions do
            printfn "Session: %s (%O)" session.Name session.Id

        printfn
            "Windows: %d; panes: %d; clients: %d"
            windows.Count
            panes.Count
            clients.Count

    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
