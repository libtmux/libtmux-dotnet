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
        let! windows = server |> Server.listWindows token
        let! panes = server |> Server.listPanes token
        let window = windows |> Seq.exactlyOne
        let pane = panes |> Seq.exactlyOne

        use! _control = server |> Control.enter token
        let! clients = server |> Server.listClients token
        let client = clients |> Seq.exactlyOne

        let! foundSession = server |> Server.tryFindSession token demo.Value.Id
        let! foundWindow = server |> Server.tryFindWindow token window.Id
        let! foundPane = server |> Server.tryFindPane token pane.Id
        let! foundClient = server |> Server.tryFindClient token client.Name

        if
            (foundSession |> Option.map (fun value -> value.Id)) <> Some demo.Value.Id
            || (foundWindow |> Option.map (fun value -> value.Id)) <> Some window.Id
            || (foundPane |> Option.map (fun value -> value.Id)) <> Some pane.Id
            || (foundClient |> Option.map (fun value -> value.Name)) <> Some client.Name
        then
            failwith "A lookup did not return its requested entity."

        let! missingSession =
            server |> Server.tryFindSession token (SessionId Int32.MaxValue)

        let! missingWindow = server |> Server.tryFindWindow token (WindowId Int32.MaxValue)
        let! missingPane = server |> Server.tryFindPane token (PaneId Int32.MaxValue)
        let! missingClient = server |> Server.tryFindClient token (client.Name + "-missing")

        if
            Option.isSome missingSession
            || Option.isSome missingWindow
            || Option.isSome missingPane
            || Option.isSome missingClient
        then
            failwith "A missing entity must return None after a successful read."

        match foundSession with
        | Some session -> printfn "Found session: %s" session.Name
        | None -> failwith "The owned session disappeared."

        printfn "Found window, pane and control client; missing lookups returned None."
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
