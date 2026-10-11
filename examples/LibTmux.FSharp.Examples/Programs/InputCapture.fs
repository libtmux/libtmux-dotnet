open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let quoteShell (value: string) = "'" + value.Replace("'", "'\\''") + "'"

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let socketName = "fsharp-capture-" + Guid.NewGuid().ToString("N")

        let options =
            ServerConnectionOptions(
                SocketName = socketName,
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        use! _session =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", Command = "/bin/sh"),
                token
            )

        let server = owned.Value
        let! panes = server |> Server.panes |> Query.list token
        let pane = panes |> Seq.exactlyOne
        let channel = "capture-" + Guid.NewGuid().ToString("N")
        use wait = server.OpenWaitChannel(channel)
        let marker = "api capture ready"

        let command =
            sprintf
                "printf '%%s\\n' %s; tmux -L %s wait-for -S %s"
                (quoteShell marker)
                (quoteShell socketName)
                (quoteShell channel)

        do! pane |> Pane.sendText token command

        do! pane |> Pane.pressKey token "Enter"
        let! ready = wait.WaitAsync(TimeSpan.FromSeconds 5., token)
        printfn "The shell signalled: %b" ready

        let! lines =
            pane
            |> Pane.capture token (CapturePaneRequest(JoinWrappedLines = true))

        // Joining wrapped lines keeps the terminal's padding after the output,
        // and only a whole line counts, so the echoed command cannot match.
        match
            lines
            |> Seq.map (fun line -> line.TrimEnd())
            |> Seq.tryFind ((=) marker)
        with
        | Some line -> printfn "Captured line: %s" line
        | None -> printfn "Captured line: none"
    }

runAsync().GetAwaiter().GetResult()
