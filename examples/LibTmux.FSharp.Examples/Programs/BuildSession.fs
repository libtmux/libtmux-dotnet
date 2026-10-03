// fsharp-snippet: BuildSession
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-build-session-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = options |> Server.createOwned token

        // Describe the session, then create it in one call: the first window is
        // the one tmux makes with the session, and each split goes beside the
        // pane before it.
        let dev =
            { SessionSpec.named "dev" with
                Windows =
                    [
                        { WindowSpec.named "editor" with
                            Command = Some "exec sleep 60"
                        }
                        { WindowSpec.named "logs" with
                            Command = Some "exec sleep 60"
                            Splits =
                                [
                                    { SplitSpec.empty with
                                        Direction = Some PaneDirection.Right
                                        Command = Some "exec sleep 60"
                                    }
                                    { SplitSpec.empty with
                                        Command = Some "exec sleep 60"
                                    }
                                ]
                        }
                    ]
            }

        let! session = owned.Value |> Server.newSession token dev

        // A chain runs in one tmux invocation; each step acts on what the one
        // before made.
        let! _ =
            owned.Value
            |> Chain.start
            |> Chain.newWindow session "watch"
            |> Chain.splitLeftRight
            |> Chain.sendLine "exec sleep 60"
            |> Chain.run token

        // Every command through this handle, and the handles taken from it,
        // gives tmux five seconds.
        let bounded = owned.Value |> Server.within (TimeSpan.FromSeconds 5.)
        let! windows = bounded |> Server.windows |> Query.list token

        for window in windows do
            let! panes = window |> Window.panes |> Query.list token
            printfn "%s: %d panes" window.Name panes.Count
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
