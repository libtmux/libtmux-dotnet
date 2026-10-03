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
                SocketName = "fsharp-snapshot-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        let! server =
            task {
                use! session =
                    owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

                let! server = LibTmux.Server.ConnectAsync(options, token)

                match server.Sessions |> Snapshot.relation with
                | Uncaptured _ -> printfn "Sessions have not been captured."
                | Captured _ -> failwith "Connecting must not capture the hierarchy."

                let! captured = server |> Server.capture token SnapshotDepth.Panes

                let capturedSession =
                    match captured.Sessions |> Snapshot.relation with
                    | Captured sessions -> sessions |> Seq.exactlyOne
                    | Uncaptured _ -> failwith "The requested sessions were not captured."

                let pane =
                    match capturedSession.ActivePane |> Snapshot.value with
                    | Captured pane -> pane
                    | Uncaptured _ -> failwith "The active pane was not captured."

                match pane |> Pane.currentPath, pane |> Pane.currentCommand with
                | Some _, Some _ -> printfn "The captured active pane has a path and command."
                | _ -> failwith "The running pane should report its path and command."

                match server.Sessions |> Snapshot.relation with
                | Uncaptured _ -> printfn "The original handle is still uncaptured."
                | Captured _ -> failwith "Capturing must return a new immutable handle."

                // Keep this private daemon alive after its only owned session is disposed.
                let! result =
                    server.ExecuteCommandAsync([| "set-option"; "-s"; "exit-empty"; "off" |], token)

                if result.ExitCode <> 0 then
                    failwithf "Could not keep the empty server alive: %A" result.StandardErrorLines

                return server
            }

        let! empty = server |> Server.capture token SnapshotDepth.Sessions

        match empty.Sessions |> Snapshot.relation with
        | Captured sessions when sessions.Count = 0 -> printfn "Captured sessions can be empty."
        | _ -> failwith "An empty captured relation must be distinct from an uncaptured relation."
    }

runAsync().GetAwaiter().GetResult()
