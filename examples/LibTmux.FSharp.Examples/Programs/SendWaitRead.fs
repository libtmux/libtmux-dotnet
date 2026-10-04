// fsharp-snippet: SendWaitRead
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
                SocketName = "fsharp-send-wait-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value.CreateSessionAsync(NewSessionRequest(Name = "work", Command = "/bin/sh"), token)

        let! pane = session |> Session.activePane token

        // Type a line and wait for what it prints. The screen before it and
        // the line's own echo do not count.
        let! ready =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 5.) "echo server ready" "server ready"

        // A condition sees every visible row each time the pane changes.
        do! pane |> Pane.sendLine token "seq 3"

        let! counted =
            pane
            |> Pane.waitUntil token (TimeSpan.FromSeconds 5.) (fun rows -> rows |> Seq.exists ((=) "3"))

        // Running a command waits for its exit status and returns its output.
        let! listing =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'a\\nb\\n'; exit 4"

        let! screen = pane |> Pane.capture token (CapturePaneRequest())

        // One case for each way a wait can end; leaving one out draws a warning.
        let describe wait =
            match wait with
            | PaneWait.Found -> "found"
            | PaneWait.Printed -> "printed"
            | PaneWait.Stopped pattern -> "stopped by " + pattern
            | PaneWait.TimedOut -> "timed out"
            | PaneWait.Ended -> "the pane's program ended"

        printfn "ready: %s" (describe ready)
        printfn "counted: %s" (describe counted)

        match listing with
        | PaneRun.Exited status -> printfn "run: exit %d, output %A" status (List.ofSeq listing.Output)
        | _ -> printfn "run: did not finish"

        printfn "screen shows the run: %b" (screen |> Seq.exists (fun row -> row = "a"))
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
