open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let socketRoot = Directory.CreateTempSubdirectory "libtmux-example-"

        use _cleanup =
            { new IDisposable with
                member _.Dispose() = socketRoot.Delete true
            }

        let options =
            ServerConnectionOptions(
                SocketPath = Path.Combine(socketRoot.FullName, "tmux.sock"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        use! _session =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), token)

        let server = owned.Value
        let! panes = server |> Server.panes |> Query.list token
        let pane = panes |> Seq.exactlyOne
        let marker = "api capture ready"

        do! pane |> Pane.sendText token (sprintf "printf '%%s\\n' '%s'" marker)

        do! pane |> Pane.pressKey token "Enter"

        // The echoed command contains the marker, so only a whole line counts as the output.
        let request =
            PaneWaitRequest(
                Patterns = [ Regex($"^{Regex.Escape marker}$", RegexOptions.CultureInvariant) ],
                Timeout = TimeSpan.FromSeconds 5.
            )

        let! printed = pane |> Pane.waitFor token request
        printfn "The pane printed the marker: %b" printed.Found

        let! lines = pane |> Pane.capture token (CapturePaneRequest(JoinWrappedLines = true))

        // Joining wrapped lines keeps the terminal's padding after the output.
        match lines |> Seq.map (fun line -> line.TrimEnd()) |> Seq.tryFind ((=) marker) with
        | Some line -> printfn "Captured line: %s" line
        | None -> printfn "Captured line: none"
    }

runAsync().GetAwaiter().GetResult()
