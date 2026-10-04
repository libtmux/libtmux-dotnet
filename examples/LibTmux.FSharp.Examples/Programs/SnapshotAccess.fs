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
                SocketName = "fsharp-snapshot-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        // A relation is either captured, holding what tmux reported, or not read at all.
        let describe (relation: CapturedRelation<'T>) =
            match relation |> Snapshot.relation with
            | Captured items -> "captured " + string items.Count
            | Uncaptured _ -> "not captured"

        let! server =
            task {
                use! session =
                    owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

                let server = owned.Value
                printfn "Connected handle's sessions: %s" (describe server.Sessions)

                let! captured = server |> Server.capture token SnapshotDepth.Panes
                printfn "Captured handle's sessions: %s" (describe captured.Sessions)

                match captured.Sessions |> Snapshot.relation with
                | Captured sessions ->
                    match (Seq.exactlyOne sessions).ActivePane |> Snapshot.value with
                    | Captured pane ->
                        printfn
                            "Active pane reports a path: %b; a command: %b"
                            (pane |> Pane.currentPath |> Option.isSome)
                            (pane |> Pane.currentCommand |> Option.isSome)
                    | Uncaptured _ -> printfn "Active pane: not captured"
                | Uncaptured _ -> ()

                // Capturing returns a new handle; the one it was taken from is unchanged.
                printfn "Original handle's sessions: %s" (describe server.Sessions)

                // Keep this private server alive after its only session is disposed.
                do! server.Options |> Options.set token (TmuxOptionKey.Flag "exit-empty") false
                return server
            }

        let! empty = server |> Server.capture token SnapshotDepth.Sessions
        printfn "Empty server's sessions: %s" (describe empty.Sessions)
    }

runAsync().GetAwaiter().GetResult()
