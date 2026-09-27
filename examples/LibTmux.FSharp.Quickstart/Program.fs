// fsharp-snippet: Quickstart
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        let binary =
            match Environment.GetEnvironmentVariable("LIBTMUX_TMUX") with
            | null
            | "" -> "tmux"
            | value -> value

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! ownedServer = LibTmux.Server.CreateOwnedAsync(options, CancellationToken.None)

        use! _ownedSession =
            ownedServer.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                CancellationToken.None
            )

        use! _ownedWorker =
            ownedServer.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "idle", Command = "/bin/sh"),
                CancellationToken.None
            )

        let! connected = LibTmux.Server.ConnectAsync(options, CancellationToken.None)

        let! captured =
            connected |> Server.capture CancellationToken.None SnapshotDepth.Panes

        let session =
            captured.Sessions |> Seq.find (fun candidate -> candidate.Name = "demo")

        let window = session.Windows |> Seq.exactlyOne
        let pane = window.Panes |> Seq.exactlyOne

        let command =
            pane
            |> Pane.currentCommand
            |> Option.defaultWith (fun () -> failwith "The captured pane has no command.")

        let localMatches =
            captured.Panes
            |> Seq.filter (fun candidate -> candidate.Id = pane.Id)
            |> Seq.length

        let hasPane =
            Filter.eq pane.Id PaneFields.id
            |> Filter.any WindowFields.panes
            |> Filter.any SessionFields.windows

        let selected =
            captured.Sessions
            |> Query.matching hasPane
            |> Selection.exactlyOne
            |> Result.defaultWith (fun error -> failwithf "Expected one matching session: %A" error)

        if
            captured.Sessions.Count <> 2
            || captured.Panes.Count <> 2
            || window.Name <> "shell"
            || localMatches <> 1
            || selected.Id <> session.Id
        then
            failwith "The captured graph and portable filter did not agree."

        printfn "%s / %s / %s" session.Name window.Name command
        printfn "local pane matches: %d" localMatches
        printfn "portable match: %s" selected.Name
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
