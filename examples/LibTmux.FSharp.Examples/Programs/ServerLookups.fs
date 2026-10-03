// fsharp-snippet: ServerLookups
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
                SocketName = "fsharp-lookups-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! windows = server |> Server.windows |> Query.list token
        let! panes = server |> Server.panes |> Query.list token
        let window = windows |> Seq.exactlyOne
        let pane = panes |> Seq.exactlyOne

        use! _control = server |> Control.enter token
        let! clients = server |> Server.clients |> Query.list token
        let client = clients |> Seq.exactlyOne

        let! foundSession = server |> Server.tryFindSession token demo.Value.Id
        let! foundWindow = server |> Server.tryFindWindow token window.Id
        let! foundPane = server |> Server.tryFindPane token pane.Id
        let! foundClient = server |> Server.tryFindClient token client.Name

        let! missingSession =
            server |> Server.tryFindSession token (SessionId Int32.MaxValue)

        let! missingWindow = server |> Server.tryFindWindow token (WindowId Int32.MaxValue)
        let! missingPane = server |> Server.tryFindPane token (PaneId Int32.MaxValue)
        let! missingClient = server |> Server.tryFindClient token (client.Name + "-missing")

        printfn "session: %A" (foundSession |> Option.map (fun found -> found.Name))
        printfn "window: %A" (foundWindow |> Option.map (fun found -> found.Name))
        printfn "pane: %A" (foundPane |> Option.map (fun found -> found.Id = pane.Id))
        printfn "client: %A" (foundClient |> Option.map (fun found -> found.Name = client.Name))

        printfn
            "missing: %A"
            [
                missingSession.IsSome
                missingWindow.IsSome
                missingPane.IsSome
                missingClient.IsSome
            ]
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
