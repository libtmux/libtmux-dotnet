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
                SocketName = "fsharp-selection-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! _demo =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "worker", Command = "/bin/cat"), token)

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! sessions = server |> Server.listSessions token

        match
            sessions
            |> Seq.filter (fun session -> session.Name = "demo")
            |> Selection.exactlyOne
        with
        | Ok session -> printfn "One match: %s" session.Name
        | Error error -> failwithf "Expected one demo session, received %A." error

        match
            sessions
            |> Seq.filter (fun session -> session.Name = "missing")
            |> Selection.exactlyOne
        with
        | Error NoMatches -> printfn "No match: NoMatches"
        | result -> failwithf "Expected NoMatches, received %A." result

        match sessions |> Selection.exactlyOne with
        | Error MultipleMatches -> printfn "Two matches: MultipleMatches"
        | result -> failwithf "Expected MultipleMatches, received %A." result
    }

runAsync().GetAwaiter().GetResult()
