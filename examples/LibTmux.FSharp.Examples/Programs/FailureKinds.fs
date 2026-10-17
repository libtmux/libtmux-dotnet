// fsharp-snippet: FailureKinds
open System
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp

// A failure says whether tmux saw the command, which decides whether sending
// it again could repeat what it did.
let kind (work: unit -> Task) =
    task {
        try
            do! work ()
            return "succeeded"
        with
        | TmuxFailure.NotSent _ -> return "NotSent"
        | TmuxFailure.Ran _ -> return "Ran"
        | TmuxFailure.MayHaveRun _ -> return "MayHaveRun"
    }

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token
        let socket = "fsharp-failure-kinds-" + Guid.NewGuid().ToString("N")

        let options =
            ServerConnectionOptions(
                SocketName = socket,
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value
            |> Server.newSession token (SessionSpec.running "jobs" "/bin/sh")

        let! pane = session |> Session.activePane token

        let! _ =
            pane
            |> Pane.sendAndWait
                token
                (TimeSpan.FromSeconds 5.)
                "echo ready"
                "ready"

        // Typed options read back as the type they were written with.
        do!
            session.Options
            |> Options.set token TmuxOptionKey.HistoryLimit 50_000

        let! history =
            session.Options |> Options.get token TmuxOptionKey.HistoryLimit

        printfn "history-limit: %d" history

        // No server listens on this socket, so no command reached one.
        let missing =
            ServerConnectionOptions(
                SocketName = socket + "-none",
                ConfigurationFile = "/dev/null"
            )

        let! notSent = kind (fun () -> missing |> Server.connect token :> Task)
        printfn "A server that is not running: %s" notSent

        // tmux ran the command and refused it.
        let refuse (cancellationToken: CancellationToken) =
            task {
                do!
                    session.Options
                    |> Options.set
                        cancellationToken
                        TmuxOptionKey.HistoryLimit
                        -1
            }

        let! ran = kind (fun () -> refuse token :> Task)
        printfn "A value tmux refuses: %s" ran

        // The command is running when the token is cancelled.
        use stop = CancellationTokenSource.CreateLinkedTokenSource(token)

        let running =
            pane
            |> Pane.run
                stop.Token
                (TimeSpan.FromSeconds 10.)
                "echo started; sleep 5"

        let! _ =
            pane |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "started"

        stop.Cancel()
        let! mayHaveRun = kind (fun () -> running :> Task)

        printfn "A run cancelled after it was sent: %s" mayHaveRun

        // Retry sends again only while nothing reached tmux, as while a
        // server is still starting.
        let delays =
            [ TimeSpan.FromMilliseconds 10.; TimeSpan.FromMilliseconds 20. ]

        let attempts = ref 0

        let count
            (operation: CancellationToken -> Task<'T>)
            (cancellationToken: CancellationToken)
            =
            attempts.Value <- attempts.Value + 1
            operation cancellationToken

        let! unsent =
            kind (fun () ->
                Retry.ifNotSentAfter
                    token
                    delays
                    (count (fun ct -> missing |> Server.connect ct))
                :> Task)

        printfn
            "Retried while NotSent: %d attempts, then %s"
            attempts.Value
            unsent

        attempts.Value <- 0

        let! refused =
            kind (fun () ->
                Retry.ifNotSentAfter token delays (count refuse) :> Task)

        printfn "Retried after Ran: %d attempt, then %s" attempts.Value refused
    }
// endfsharp-snippet

runAsync().GetAwaiter().GetResult()
