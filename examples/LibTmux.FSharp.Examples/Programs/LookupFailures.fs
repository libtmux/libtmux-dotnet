open System
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        // LIBTMUX_TMUX picks the tmux CI is testing; without it, the tmux on PATH.
        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-failures-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        // Absence is None; every other failure is an exception, named here by
        // the type a caller would catch.
        let failure (lookup: unit -> Task<'T option>) =
            task {
                try
                    let! found = lookup ()
                    return if found.IsSome then "found" else "None"
                with
                | :? OperationCanceledException -> return "OperationCanceledException"
                | :? ArgumentException -> return "ArgumentException"
                | :? LibTmuxException -> return "LibTmuxException"
            }

        let! stoppedServer =
            task {
                use! owned = options |> Server.createOwned token

                use! session =
                    owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

                let! server = options |> Server.connect token

                let! missing =
                    failure (fun () -> server |> Server.tryFindSession token (SessionId Int32.MaxValue))

                printfn "A session that does not exist: %s" missing

                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                let! canceled =
                    failure (fun () -> server |> Server.tryFindSession cancelled.Token session.Value.Id)

                printfn "A cancelled lookup: %s" canceled

                let! blank = failure (fun () -> server |> Server.tryFindClient token " ")
                printfn "A blank client name: %s" blank
                return server
            }

        let! stopped =
            failure (fun () -> stoppedServer |> Server.tryFindSession token (SessionId Int32.MaxValue))

        printfn "A server that has stopped: %s" stopped
    }

runAsync().GetAwaiter().GetResult()
