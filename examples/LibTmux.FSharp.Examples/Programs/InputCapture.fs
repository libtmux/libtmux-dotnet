open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let quoteShell (value: string) = "'" + value.Replace("'", "'\\''") + "'"

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let socketName = "fsharp-capture-" + Guid.NewGuid().ToString("N")

        let options =
            ServerConnectionOptions(SocketName = socketName, ConfigurationFile = "/dev/null", TmuxBinaryPath = binary)

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! _session =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), token)

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! panes = server |> Server.panes |> Query.list token
        let pane = panes |> Seq.exactlyOne
        let channel = "capture-" + Guid.NewGuid().ToString("N")
        use wait = server.OpenWaitChannel(channel)
        let marker = "api capture ready"

        let command =
            sprintf
                "printf '%%s\\n' %s; %s -L %s wait-for -S %s"
                (quoteShell marker)
                (quoteShell binary)
                (quoteShell socketName)
                (quoteShell channel)

        do!
            pane
            |> Pane.sendKeys token (SendKeysRequest(Text = command, Literal = true, Enter = false))

        do! pane |> Pane.sendKeys token (SendKeysRequest(Text = "Enter", Enter = false))
        let! ready = wait.WaitAsync(TimeSpan.FromSeconds 5., token)

        if not ready then
            failwith "The pane did not signal that its output was ready."

        let! lines = pane |> Pane.capture token (CapturePaneRequest(JoinWrappedLines = true))

        // Joining wrapped lines preserves terminal padding after the output.
        if not (lines |> Seq.exists (fun line -> line.TrimEnd() = marker)) then
            failwithf "Capture did not contain the complete output line: %A" lines

        printfn "%s" marker
    }

runAsync().GetAwaiter().GetResult()
