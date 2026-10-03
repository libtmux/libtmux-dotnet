// fsharp-snippet: LiveState
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
                SocketName = "fsharp-live-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        let! session =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(Name = "work", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        // Captures again on each change tmux announces, and every 200 ms for
        // changes it does not, such as the command a pane runs.
        use! mirror =
            session |> Mirror.startRefreshing token (TimeSpan.FromMilliseconds 200.)

        let! _ =
            session.CreateWindowAsync(NewWindowRequest(Name = "logs", Command = "/bin/sh"), token)

        let! withLogs =
            mirror
            |> Mirror.waitUntil token (TimeSpan.FromSeconds 5.) (fun view ->
                view.Server.Windows |> Seq.exists (fun window -> window.Name = "logs"))

        let! panes = session |> Session.panes |> Query.list token

        do!
            panes[0]
            |> Pane.sendKeys token (SendKeysRequest(Text = "exec sleep 30", Literal = true))

        let! sleeping =
            mirror
            |> Mirror.waitUntil token (TimeSpan.FromSeconds 5.) (fun view ->
                view.Server.Panes |> Seq.exists (fun pane -> pane.CurrentCommand = "sleep"))

        printfn "windows: %s" (String.Join(", ", [ for window in withLogs.Server.Windows -> window.Name ]))

        printfn
            "sleeping panes: %d"
            (sleeping.Server.Panes
             |> Seq.filter (fun pane -> pane.CurrentCommand = "sleep")
             |> Seq.length)

        printfn "newer view: %b" (sleeping.Epoch > withLogs.Epoch)
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
