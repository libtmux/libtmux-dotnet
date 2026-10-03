open System
open System.Threading
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
                SocketName = "fsharp-selection-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = options |> Server.createOwned token

        use! _demo =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/cat"), token)

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(NewSessionRequest(Name = "worker", Command = "/bin/cat"), token)

        let server = owned.Value
        let! sessions = server |> Server.sessions |> Query.list token

        // Exactly one match is Ok; none and several are distinct errors.
        let describe (result: Result<LibTmux.Session, CardinalityError>) =
            match result with
            | Ok session -> "Ok " + session.Name
            | Error NoMatches -> "Error NoMatches"
            | Error MultipleMatches -> "Error MultipleMatches"

        let named name =
            sessions |> Seq.filter (fun session -> session.Name = name)

        printfn "Sessions named demo: %s" (named "demo" |> Selection.exactlyOne |> describe)
        printfn "Sessions named missing: %s" (named "missing" |> Selection.exactlyOne |> describe)
        printfn "Every session: %s" (sessions |> Selection.exactlyOne |> describe)
    }

runAsync().GetAwaiter().GetResult()
