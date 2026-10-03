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
                SocketName = "fsharp-failures-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        let! stoppedServer =
            task {
                use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

                use! session =
                    owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

                let! server = LibTmux.Server.ConnectAsync(options, token)
                let! missing = server |> Server.tryFindSession token (SessionId Int32.MaxValue)

                match missing with
                | None -> printfn "A successful lookup can return None."
                | Some _ -> failwith "The missing session unexpectedly exists."

                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                try
                    let! _ = server |> Server.tryFindSession cancelled.Token session.Value.Id
                    failwith "A cancelled lookup must throw."
                with :? OperationCanceledException ->
                    printfn "Cancellation remains OperationCanceledException."

                try
                    let! _ = server |> Server.tryFindClient token " "
                    failwith "A blank client name must be rejected."
                with :? ArgumentException ->
                    printfn "A blank client name remains ArgumentException."

                return server
            }

        try
            let! _ = stoppedServer |> Server.tryFindSession token (SessionId Int32.MaxValue)
            failwith "A failed server read must not become None."
        with :? LibTmuxException ->
            printfn "A stopped server remains a LibTmuxException."
    }

runAsync().GetAwaiter().GetResult()
