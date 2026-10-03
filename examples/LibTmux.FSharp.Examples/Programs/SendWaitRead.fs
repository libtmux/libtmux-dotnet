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
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        let! session =
            owned.Value.CreateSessionAsync(NewSessionRequest(Name = "work", Command = "/bin/sh"), token)

        let! panes = session |> Session.panes |> Query.list token
        let pane = panes[0]

        // Send, then wait for the pane's own output instead of sleeping.
        do!
            pane
            |> Pane.sendKeys token (SendKeysRequest(Text = "printf 'server %s\\n' ready", Literal = true))

        let! ready = pane |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "server ready"

        // A condition sees every visible row each time the pane changes.
        do! pane |> Pane.sendKeys token (SendKeysRequest(Text = "seq 3", Literal = true))

        let! counted =
            pane
            |> Pane.waitUntil token (TimeSpan.FromSeconds 5.) (fun rows -> rows |> Seq.exists ((=) "3"))

        // Running a command waits for its exit status and returns its output.
        let! listing =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'a\\nb\\n'; exit 4"

        let! screen = pane |> Pane.capture token (CapturePaneRequest())

        printfn "ready: %A" ready.Outcome
        printfn "counted: %A" counted.Outcome
        printfn "run: exit %A, output %A" listing.ExitStatus (List.ofSeq listing.Output)
        printfn "screen rows: %d" (screen |> Seq.filter (String.IsNullOrWhiteSpace >> not) |> Seq.length)

        if
            not ready.Found
            || not counted.Found
            || listing.ExitStatus <> Nullable 4
            || List.ofSeq listing.Output <> [ "a"; "b" ]
        then
            failwith "The pane did not print what the commands sent to it."
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
